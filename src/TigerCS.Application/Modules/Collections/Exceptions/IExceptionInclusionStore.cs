using TigerCS.Domain.Modules.Collections.Review;

namespace TigerCS.Application.Modules.Collections.Exceptions;

/// <summary>Persistence of manual GULF_CUSTOMERS inclusions. Writes are staged on the shared unit of work; the caller saves them together with the audit entry.</summary>
public interface IExceptionInclusionStore
{
    /// <summary>The active (not removed) inclusions of one campaign.</summary>
    Task<IReadOnlyList<CollectionsExceptionInclusion>> ListActiveAsync(string campaignKey, CancellationToken ct);

    /// <summary>Stages a new inclusion unless an active one already exists for the same unit and campaign; false when it already exists.</summary>
    Task<bool> StageAddAsync(CollectionsExceptionInclusion inclusion, CancellationToken ct);

    /// <summary>Stages the removal of the active inclusion of the unit (kept as history); false when none exists.</summary>
    Task<bool> StageRemoveAsync(string campaignKey, int companyId, string tenantId, string unitCode, Guid removedBy, DateTime nowUtc, CancellationToken ct);
}
