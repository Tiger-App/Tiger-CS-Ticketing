using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Application.Modules.Ticketing.ActivityTimeline;

/// <summary>
/// Read model behind the Activity tab: loads every store that records something about the ticket and hands it to the pure
/// <see cref="TicketActivityTimelineBuilder"/>. Strictly read-only — it writes nothing, rewrites nothing and creates no history.
///
/// <para>
/// <b>Same visibility rule as the ticket itself</b> (<c>TicketQueryAppService.CanViewTicketAsync</c>): the timeline can never be a side
/// door onto a ticket the caller cannot open.
/// </para>
/// </summary>
public sealed class TicketActivityTimelineAppService(
    ITicketRepository ticketRepository,
    ITicketInteractionRepository interactionRepository,
    IGenesysConversationRepository conversationRepository,
    IChannelRepository channelRepository,
    ITicketAgentHandoffRepository handoffRepository,
    ITicketInteractionHandlerRepository handlerRepository,
    ITicketWorkflowEventRepository workflowEventRepository,
    ITicketStatusHistoryRepository statusHistoryRepository,
    ITicketAssignmentRepository assignmentRepository,
    IAuditEntryReader auditReader,
    IEmployeeRepository employeeRepository,
    IDepartmentRepository departmentRepository,
    IUserRoleReader roleReader,
    TicketQueryAppService ticketQueryAppService)
{
    public async Task<TicketQueryResultDto<TicketActivityTimelineDto>> GetAsync(
        Guid callerEmployeeId,
        IReadOnlyCollection<string> callerRoles,
        long ticketId,
        CancellationToken cancellationToken = default)
    {
        var ticket = await ticketRepository.GetByIdAsync(ticketId, cancellationToken);
        if (ticket is null)
        {
            return TicketQueryResultDto<TicketActivityTimelineDto>.Failure(TicketQueryOutcome.NotFound);
        }

        if (!await ticketQueryAppService.CanViewTicketAsync(callerEmployeeId, callerRoles, ticket, cancellationToken))
        {
            return TicketQueryResultDto<TicketActivityTimelineDto>.Failure(TicketQueryOutcome.Forbidden);
        }

        var interactions = await interactionRepository.ListByTicketIdAsync(ticketId, cancellationToken);
        var messages = interactions.Count == 0
            ? new Dictionary<long, IReadOnlyList<Domain.Modules.Ticketing.TicketInteractionMessage>>()
            : new Dictionary<long, IReadOnlyList<Domain.Modules.Ticketing.TicketInteractionMessage>>(
                await conversationRepository.ListMessagesForInteractionsAsync([.. interactions.Select(i => i.TicketInteractionId)], cancellationToken));

        var channelNames = new Dictionary<byte, string>();
        foreach (var channelId in interactions.Select(i => i.ChannelId).Distinct())
        {
            if ((await channelRepository.GetByIdAsync(channelId, cancellationToken))?.Name is { } name)
            {
                channelNames[channelId] = name;
            }
        }

        var handoffs = await handoffRepository.ListByTicketIdAsync(ticketId, cancellationToken);
        var handlers = await handlerRepository.ListByTicketIdAsync(ticketId, cancellationToken);
        var workflowEvents = await workflowEventRepository.ListByTicketIdAsync(ticketId, cancellationToken);
        var history = await statusHistoryRepository.ListByTicketIdAsync(ticketId, cancellationToken);
        var assignments = await assignmentRepository.ListByTicketIdAsync(ticketId, cancellationToken);

        var conversationIds = interactions.Select(i => i.GenesysConversationId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var audit = await auditReader.ListForTicketAsync(ticketId, conversationIds, TimelineAuditActions.All, cancellationToken);

        // Everyone the timeline names, resolved once from the employee directory (so a person who has since left is still named).
        var employeeIds = new HashSet<Guid>();
        void Add(Guid? id)
        {
            if (id is { } known)
            {
                employeeIds.Add(known);
            }
        }

        Add(ticket.CreatedByEmployeeId);
        handlers.ToList().ForEach(h => Add(h.HandledByUserId));
        interactions.ToList().ForEach(i => Add(i.HandledByUserId));
        handoffs.ToList().ForEach(h => Add(h.AssignedEmployeeId));
        workflowEvents.ToList().ForEach(e => { Add(e.ActorEmployeeId); Add(e.ToEmployeeId); });
        history.ToList().ForEach(h => Add(h.ActorEmployeeId));
        assignments.ToList().ForEach(a => { Add(a.AssignedEmployeeId); Add(a.AssigningActorEmployeeId); });

        var names = new Dictionary<Guid, string>();
        var supervisors = new HashSet<Guid>();
        foreach (var id in employeeIds)
        {
            if ((await employeeRepository.GetByIdAsync(id, cancellationToken))?.DisplayName is { Length: > 0 } displayName)
            {
                names[id] = displayName;
            }
        }

        // Only a closing human's role matters ("Supervisor" vs "Human Agent"), so roles are read for them alone.
        foreach (var closer in history
                     .Where(h => h.NewValue == (byte)Domain.Modules.Ticketing.TicketStatus.Closed && !h.ActorIsSystem && h.ActorEmployeeId is not null)
                     .Select(h => h.ActorEmployeeId!.Value)
                     .Distinct())
        {
            var roles = await roleReader.GetRolesAsync(closer, cancellationToken);
            if (roles.Any(r => r is Roles.CsSupervisor or Roles.CsManager or Roles.GeneralManager))
            {
                supervisors.Add(closer);
            }
        }

        var departmentIds = new HashSet<int> { ticket.CurrentDepartmentId };
        assignments.ToList().ForEach(a => departmentIds.Add(a.AssignedDepartmentId));
        handoffs.ToList().ForEach(h => departmentIds.Add(h.DepartmentId));
        foreach (var evt in workflowEvents)
        {
            if (evt.FromDepartmentId is { } from) { departmentIds.Add(from); }
            if (evt.ToDepartmentId is { } to) { departmentIds.Add(to); }
        }

        var departmentNames = new Dictionary<int, string>();
        foreach (var id in departmentIds)
        {
            if ((await departmentRepository.GetByIdAsync(id, cancellationToken))?.Name is { } departmentName)
            {
                departmentNames[id] = departmentName;
            }
        }

        var timeline = TicketActivityTimelineBuilder.Build(new TicketTimelineSources
        {
            TicketId = ticket.TicketId,
            TicketNumber = ticket.TicketNumber,
            CreatedAtUtc = ticket.CreatedAtUtc,
            CreatedByEmployeeId = ticket.CreatedByEmployeeId,
            Interactions = interactions,
            MessagesByInteraction = messages,
            ChannelNames = channelNames,
            Handoffs = handoffs,
            Handlers = handlers,
            WorkflowEvents = workflowEvents,
            StatusHistory = history,
            Assignments = assignments,
            AuditEntries = audit,
            EmployeeNames = names,
            DepartmentNames = departmentNames,
            SupervisorIds = supervisors
        });

        return TicketQueryResultDto<TicketActivityTimelineDto>.Success(timeline);
    }
}
