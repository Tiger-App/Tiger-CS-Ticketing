using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface ITicketAssignmentRepository
{
    Task<TicketAssignment?> GetCurrentAsync(long ticketId, CancellationToken cancellationToken = default);

    /// <summary>True when the employee held this ticket before (a superseded assignment) — used to deny a previous owner any right that rests on having handled the ticket once the ticket has moved on.</summary>
    Task<bool> HasSupersededAssignmentAsync(long ticketId, Guid employeeId, CancellationToken cancellationToken = default);

    /// <summary>Every assignment the ticket ever had (current and superseded), oldest first — read-only, for the Activity timeline's ownership history.</summary>
    Task<IReadOnlyList<TicketAssignment>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The employee on each ticket's MOST RECENT assignment row (current or superseded) - who owned it last. Tickets that never had an assignment are omitted.
    /// Read-only; used to show the historical owner of a Closed ticket (closing clears the current-owner field but never the assignment history). The default
    /// implementation asks one ticket at a time; the EF repository overrides it with a single grouped read.
    /// </summary>
    async Task<IReadOnlyDictionary<long, Guid>> GetLastAssignedEmployeesAsync(IReadOnlyCollection<long> ticketIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<long, Guid>();
        foreach (var id in ticketIds.Distinct())
        {
            var last = (await ListByTicketIdAsync(id, cancellationToken)).OrderByDescending(a => a.TicketAssignmentId).FirstOrDefault();
            if (last is not null)
            {
                result[id] = last.AssignedEmployeeId;
            }
        }

        return result;
    }

    Task AddAsync(TicketAssignment assignment, CancellationToken cancellationToken = default);
}
