using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Administration.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Administration;

/// <summary>
/// Read-only dependency scan behind the department deactivation guard. It looks only at LIVE routing and approval dependencies;
/// Closed tickets and decided/historical rows are deliberately ignored (they keep their department reference forever).
/// </summary>
public sealed class DepartmentDeactivationDependencyReader(TigerCsDbContext dbContext) : IDepartmentDeactivationDependencyReader
{
    public async Task<IReadOnlyList<string>> GetBlockersAsync(int departmentId, CancellationToken cancellationToken = default)
    {
        var blockers = new List<string>();

        var openTickets = await dbContext.Tickets.AsNoTracking()
            .CountAsync(t => t.CurrentDepartmentId == departmentId && t.TicketStatus != TicketStatus.Closed, cancellationToken);
        if (openTickets > 0)
        {
            blockers.Add($"{openTickets} ticket(s) that are not Closed are still in this department. Transfer them first.");
        }

        var requestTypes = await dbContext.RequestTypes.AsNoTracking()
            .CountAsync(r => r.DepartmentId == departmentId && r.IsActive, cancellationToken);
        if (requestTypes > 0)
        {
            blockers.Add($"{requestTypes} active request type(s) still route to this department. Deactivate or move them first.");
        }

        var requirements = await dbContext.RequestTypeApprovalRequirements.AsNoTracking()
            .CountAsync(r => r.TargetDepartmentId == departmentId && r.IsActive, cancellationToken);
        if (requirements > 0)
        {
            blockers.Add($"{requirements} active approval requirement(s) target this department. Remove or redirect them first.");
        }

        var pendingApprovals = await dbContext.TicketApprovals.AsNoTracking()
            .CountAsync(a => a.TargetDepartmentId == departmentId && a.Status == ApprovalStatus.Pending, cancellationToken);
        if (pendingApprovals > 0)
        {
            blockers.Add($"{pendingApprovals} pending approval(s) are waiting on this department. Decide them first.");
        }

        var queueMappings = await dbContext.GenesysQueueMappings.AsNoTracking()
            .CountAsync(m => m.DepartmentId == departmentId && m.IsActive, cancellationToken);
        if (queueMappings > 0)
        {
            blockers.Add($"{queueMappings} active Genesys queue mapping(s) route to this department.");
        }

        var transferGrants = await dbContext.UserTransferDepartments.AsNoTracking()
            .CountAsync(g => g.DepartmentId == departmentId, cancellationToken);
        if (transferGrants > 0)
        {
            blockers.Add($"{transferGrants} user transfer-destination grant(s) still name this department.");
        }

        return blockers;
    }
}
