using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface IAssignmentRotationRepository
{
    /// <summary>
    /// The department's rotation cursor, created on first use and locked for the rest of the caller's transaction so two
    /// concurrent assignments serialize on it and can never pick the same slot. Must be called inside a transaction.
    /// </summary>
    Task<DepartmentAssignmentRotation> GetForUpdateAsync(int departmentId, DateTime nowUtc, CancellationToken cancellationToken = default);
}
