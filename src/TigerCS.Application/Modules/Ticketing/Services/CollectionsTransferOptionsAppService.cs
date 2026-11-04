using TigerCS.Application.Modules.Collections.TowerAssignments;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>The Tower and Unit choices of a move into Collections for one ticket (tower first, then that tower's units), read from the ticket's customer in CRM. Same visibility as the ticket itself; read-only.</summary>
public sealed class CollectionsTransferOptionsAppService(
    ITicketRepository ticketRepository,
    TicketQueryAppService ticketQueryAppService,
    CollectionsTransferSelectionService selection,
    CollectionsTeam team)
{
    public async Task<TicketQueryResultDto<CollectionsTransferOptionsDto>> GetAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, long ticketId, CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketQueryResultDto<CollectionsTransferOptionsDto>.Failure(TicketQueryOutcome.NotFound);
        }

        if (!await ticketQueryAppService.CanViewTicketAsync(callerEmployeeId, callerRoles, ticket, cancellationToken))
        {
            return TicketQueryResultDto<CollectionsTransferOptionsDto>.Failure(TicketQueryOutcome.Forbidden);
        }

        var options = await selection.GetOptionsAsync(ticket, cancellationToken);
        var collections = await team.GetDepartmentAsync(cancellationToken);
        return TicketQueryResultDto<CollectionsTransferOptionsDto>.Success(options with { CollectionsDepartmentId = collections?.DepartmentId });
    }
}
