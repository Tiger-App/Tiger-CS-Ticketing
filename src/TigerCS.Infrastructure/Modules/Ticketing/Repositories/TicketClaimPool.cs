using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

/// <summary>
/// Single-ticket form of <see cref="TicketQueryFilters.AvailableToTake"/>: the reasons are read from the same predicates the list uses (the
/// owner and current-assignment test, <see cref="TicketQueryFilters.HasPendingApproval"/>, <see cref="TicketQueryFilters.HasActiveAiConversation"/>).
///
/// <para>
/// Each check is its own small query, and the whole read is retried if SQL Server picks it as a <b>deadlock victim</b> (error 1205): while
/// agents race for one ticket the winner's writes and these reads touch the same tables, a read is the cheapest transaction for the engine
/// to sacrifice, and repeating a read is always safe. (A deadlock on the WRITE is translated by the unit of work and answered as a normal
/// 409 conflict.)
/// </para>
/// </summary>
public sealed class TicketClaimPool(TigerCsDbContext dbContext) : ITicketClaimPool
{
    private const int DeadlockVictim = 1205;
    private const int MaxAttempts = 4;

    public async Task<TicketClaimStatus> GetStatusAsync(long ticketId, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ReadStatusAsync(ticketId, cancellationToken);
            }
            catch (SqlException ex) when (ex.Number == DeadlockVictim && attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(15 * attempt), cancellationToken);
            }
        }
    }

    private async Task<TicketClaimStatus> ReadStatusAsync(long ticketId, CancellationToken cancellationToken)
    {
        var ticket = await dbContext.Tickets.AsNoTracking()
            .Where(t => t.TicketId == ticketId)
            .Select(t => new { t.TicketStatus, t.CurrentOwnerEmployeeId })
            .FirstOrDefaultAsync(cancellationToken);
        if (ticket is null)
        {
            return TicketClaimStatus.NotFound;
        }

        if (ticket.TicketStatus is TicketStatus.Closed or TicketStatus.Resolved)
        {
            return TicketClaimStatus.Terminal;
        }

        if (ticket.CurrentOwnerEmployeeId is not null
            || await dbContext.TicketAssignments.AsNoTracking().AnyAsync(a => a.TicketId == ticketId && a.IsCurrent, cancellationToken))
        {
            return TicketClaimStatus.Owned;
        }

        var tickets = dbContext.Tickets.AsNoTracking().Where(t => t.TicketId == ticketId);
        if (await tickets.Where(TicketQueryFilters.HasPendingApproval(dbContext)).AnyAsync(cancellationToken))
        {
            return TicketClaimStatus.PendingApproval;
        }

        return await tickets.Where(TicketQueryFilters.HasActiveAiConversation(dbContext)).AnyAsync(cancellationToken)
            ? TicketClaimStatus.ActiveAiConversation
            : TicketClaimStatus.Available;
    }
}
