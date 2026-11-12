using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>Read model of the assignment event log for Ticket Details. Same visibility rule as the ticket itself, so it is never a side door onto a ticket the caller cannot open. Read-only.</summary>
public sealed class TicketAssignmentHistoryAppService(
    ITicketRepository ticketRepository,
    ITicketAssignmentHistoryRepository historyRepository,
    IEmployeeRepository employeeRepository,
    IDepartmentRepository departmentRepository,
    TicketQueryAppService ticketQueryAppService)
{
    public async Task<TicketQueryResultDto<IReadOnlyList<TicketAssignmentHistoryItemDto>>> ListAsync(
        Guid callerEmployeeId, IReadOnlyCollection<string> callerRoles, long ticketId, CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketQueryResultDto<IReadOnlyList<TicketAssignmentHistoryItemDto>>.Failure(TicketQueryOutcome.NotFound);
        }

        if (!await ticketQueryAppService.CanViewTicketAsync(callerEmployeeId, callerRoles, ticket, cancellationToken))
        {
            return TicketQueryResultDto<IReadOnlyList<TicketAssignmentHistoryItemDto>>.Failure(TicketQueryOutcome.Forbidden);
        }

        var entries = await historyRepository.ListByTicketIdAsync(ticketId, cancellationToken);
        var names = new Dictionary<Guid, string>();
        async Task<string> Name(Guid id) =>
            names.TryGetValue(id, out var n) ? n : names[id] = (await employeeRepository.GetByIdAsync(id, cancellationToken))?.DisplayName ?? id.ToString();
        var departmentNames = new Dictionary<int, string>();
        async Task<string> Department(int id) =>
            departmentNames.TryGetValue(id, out var n) ? n : departmentNames[id] = (await departmentRepository.GetByIdAsync(id, cancellationToken))?.Name ?? id.ToString();

        var items = new List<TicketAssignmentHistoryItemDto>();
        foreach (var e in entries)
        {
            items.Add(new(
                e.TicketAssignmentHistoryEntryId, e.ChangedAtUtc, e.Kind.ToString(),
                e.PreviousEmployeeId, e.PreviousEmployeeId is { } p ? await Name(p) : null, e.NewEmployeeId, await Name(e.NewEmployeeId),
                e.DepartmentId, await Department(e.DepartmentId),
                e.TowerCode, e.TowerName, e.UnitNumber,
                e.ChangedByEmployeeId, e.ChangedByEmployeeId is { } c ? await Name(c) : "System", e.Reason,
                e.Source?.ToString(), e.SourceEventAtUtc, e.GenesysConversationId));
        }

        return TicketQueryResultDto<IReadOnlyList<TicketAssignmentHistoryItemDto>>.Success(items);
    }
}
