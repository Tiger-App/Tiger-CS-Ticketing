using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Abstractions;

public interface ITicketAssignmentRepository
{
    Task<TicketAssignment?> GetCurrentAsync(long ticketId, CancellationToken cancellationToken = default);

    /// <summary>True when the employee held this ticket before (a superseded assignment) — used to deny a previous owner any right that rests on having handled the ticket once the ticket has moved on.</summary>
    Task<bool> HasSupersededAssignmentAsync(long ticketId, Guid employeeId, CancellationToken cancellationToken = default);

    Task AddAsync(TicketAssignment assignment, CancellationToken cancellationToken = default);
}
