using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

public sealed class AssignmentRotationRepository(TigerCsDbContext dbContext) : IAssignmentRotationRepository
{
    public async Task<DepartmentAssignmentRotation> GetForUpdateAsync(
        int departmentId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        if (dbContext.Database.IsRelational())
        {
            // Create-if-missing, then take an update lock on the cursor row that is held until the caller's transaction ends:
            // a concurrent assignment for the same department blocks here, then reads the already-advanced cursor.
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                IF NOT EXISTS (SELECT 1 FROM DepartmentAssignmentRotations WITH (UPDLOCK, HOLDLOCK) WHERE DepartmentId = {departmentId})
                    INSERT INTO DepartmentAssignmentRotations (DepartmentId, LastAssignedEmployeeId, UpdatedAtUtc) VALUES ({departmentId}, NULL, {nowUtc})
                """,
                cancellationToken);

            return await dbContext.DepartmentAssignmentRotations
                .FromSqlInterpolated($"SELECT * FROM DepartmentAssignmentRotations WITH (UPDLOCK, ROWLOCK) WHERE DepartmentId = {departmentId}")
                .AsTracking()
                .SingleAsync(cancellationToken);
        }

        var rotation = await dbContext.DepartmentAssignmentRotations.FirstOrDefaultAsync(r => r.DepartmentId == departmentId, cancellationToken);
        if (rotation is null)
        {
            rotation = new DepartmentAssignmentRotation(departmentId, nowUtc);
            await dbContext.DepartmentAssignmentRotations.AddAsync(rotation, cancellationToken);
        }

        return rotation;
    }
}
