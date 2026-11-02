using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Ticketing.Fakes;

/// <summary>In-memory <see cref="IAssignmentRotationRepository"/>: one cursor row per department, created on first use.</summary>
public sealed class FakeAssignmentRotationRepository : IAssignmentRotationRepository
{
    public Dictionary<int, DepartmentAssignmentRotation> Rows { get; } = [];

    public Task<DepartmentAssignmentRotation> GetForUpdateAsync(int departmentId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (!Rows.TryGetValue(departmentId, out var row))
        {
            Rows[departmentId] = row = new DepartmentAssignmentRotation(departmentId, nowUtc);
        }

        return Task.FromResult(row);
    }
}
