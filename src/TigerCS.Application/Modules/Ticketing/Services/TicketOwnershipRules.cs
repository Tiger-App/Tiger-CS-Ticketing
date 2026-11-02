using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Two ownership questions the Genesys → Call Center workflow turns on, kept in one place so routing, authorization and the UI flag agree.
/// </summary>
public static class TicketOwnershipRules
{
    /// <summary>
    /// True only when the ticket's current owner was put there by AUTOMATION and no person has touched it since: the current assignment row
    /// names that owner with a null assigning actor (the system — rule/Round Robin), the ticket is still Open, and no human response has been
    /// recorded. Anything else — a manual assignment, a self-assignment, an accepted handoff, work started, a reply sent, or a missing
    /// assignment row (legacy data) — is treated as genuine human ownership. When in doubt the answer is false, so ownership is never
    /// overwritten on a guess.
    /// </summary>
    public static async Task<bool> IsAutomaticPreAssignmentAsync(
        ITicketAssignmentRepository? assignmentRepository, Ticket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (assignmentRepository is null
            || ticket.CurrentOwnerEmployeeId is not { } owner
            || ticket.TicketStatus != TicketStatus.Open
            || ticket.FirstHumanResponseAtUtc is not null)
        {
            return false;
        }

        var current = await assignmentRepository.GetCurrentAsync(ticket.TicketId, cancellationToken);
        return current is { IsCurrent: true, AssigningActorEmployeeId: null } && current.AssignedEmployeeId == owner;
    }

    /// <summary>
    /// Interaction-based authority for ONE ticket: a CS Agent whom Genesys reported as actually handling this ticket's interaction
    /// (a recorded <see cref="TicketInteractionHandler"/>), who belongs to the ticket's current department, may Resolve, Close or Transfer
    /// it while nobody else owns it (unowned, or already theirs). It never reaches a ticket another person owns, a ticket outside their
    /// department, a closed ticket, or any ticket they did not handle — so it is not a global CS Agent permission.
    /// </summary>
    public static async Task<bool> IsActingInteractionHandlerAsync(
        ITicketInteractionHandlerRepository? handlerRepository,
        IUserDepartmentAssignmentRepository userDepartmentAssignmentRepository,
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        Ticket ticket,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callerRoles);
        ArgumentNullException.ThrowIfNull(ticket);

        return handlerRepository is not null
            && callerRoles.Contains(Roles.CsAgent, StringComparer.Ordinal)
            && !Roles.IsReadOnlyCaller(callerRoles)
            && ticket.TicketStatus != TicketStatus.Closed
            && (ticket.CurrentOwnerEmployeeId is null || ticket.CurrentOwnerEmployeeId == callerEmployeeId)
            && await userDepartmentAssignmentRepository.ExistsAsync(callerEmployeeId, ticket.CurrentDepartmentId, cancellationToken)
            && await handlerRepository.HasHandledAsync(ticket.TicketId, callerEmployeeId, cancellationToken);
    }
}
