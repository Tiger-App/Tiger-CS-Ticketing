using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Reporting.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Ticketing.Repositories;

/// <summary>
/// Read-only evidence for <see cref="UnassignedTicketClassifier"/>. The ticket page is read first (Open, no owner), then each kind of evidence
/// is fetched once for the whole page (never per ticket) and combined in memory, so the same code runs on SQL Server and SQLite and the page
/// size bounds the work. Nothing is written.
/// </summary>
public sealed class TicketUnassignedWorkRepository(TigerCsDbContext db) : ITicketUnassignedWorkRepository
{
    private static readonly WorkflowEventType[] HumanEventTypes =
        [WorkflowEventType.HandoffAssigned, WorkflowEventType.HandoffStarted, WorkflowEventType.GenesysAgentConnected, WorkflowEventType.GenesysCallTransferred];

    public async Task<IReadOnlyList<UnassignedTicketEvidence>> ListOpenUnownedAsync(long afterTicketId, int limit, CancellationToken cancellationToken = default)
    {
        var tickets = await db.Tickets.AsNoTracking()
            .Where(t => t.TicketId > afterTicketId && t.TicketStatus == TicketStatus.Open && t.CurrentOwnerEmployeeId == null)
            .OrderBy(t => t.TicketId)
            .Take(limit)
            .Select(t => new TicketRow(t.TicketId, t.TicketNumber, t.TicketStatus, t.CurrentOwnerEmployeeId, t.CurrentDepartmentId, t.OriginatingDepartmentId, t.CreatedAtUtc, t.FirstHumanResponseAtUtc))
            .ToListAsync(cancellationToken);
        return await BuildAsync(tickets, cancellationToken);
    }

    public async Task<IReadOnlyList<UnassignedTicketEvidence>> ListActiveUnownedAsync(long afterTicketId, int limit, CancellationToken cancellationToken = default)
    {
        var tickets = await db.Tickets.AsNoTracking()
            .Where(t => t.TicketId > afterTicketId && t.CurrentOwnerEmployeeId == null
                && t.TicketStatus != TicketStatus.Resolved && t.TicketStatus != TicketStatus.Closed)
            .OrderBy(t => t.TicketId)
            .Take(limit)
            .Select(t => new TicketRow(t.TicketId, t.TicketNumber, t.TicketStatus, t.CurrentOwnerEmployeeId, t.CurrentDepartmentId, t.OriginatingDepartmentId, t.CreatedAtUtc, t.FirstHumanResponseAtUtc))
            .ToListAsync(cancellationToken);
        return await BuildAsync(tickets, cancellationToken);
    }

    public async Task<UnassignedTicketEvidence?> GetEvidenceAsync(long ticketId, CancellationToken cancellationToken = default)
    {
        var tickets = await db.Tickets.AsNoTracking()
            .Where(t => t.TicketId == ticketId)
            .Select(t => new TicketRow(t.TicketId, t.TicketNumber, t.TicketStatus, t.CurrentOwnerEmployeeId, t.CurrentDepartmentId, t.OriginatingDepartmentId, t.CreatedAtUtc, t.FirstHumanResponseAtUtc))
            .ToListAsync(cancellationToken);
        return (await BuildAsync(tickets, cancellationToken)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<UnassignedTicketEvidence>> ListEvidenceAsync(IReadOnlyCollection<long> ticketIds, CancellationToken cancellationToken = default)
    {
        if (ticketIds.Count == 0)
        {
            return [];
        }

        var wanted = ticketIds.Distinct().ToList();
        var tickets = await db.Tickets.AsNoTracking()
            .Where(t => wanted.Contains(t.TicketId))
            .Select(t => new TicketRow(t.TicketId, t.TicketNumber, t.TicketStatus, t.CurrentOwnerEmployeeId, t.CurrentDepartmentId, t.OriginatingDepartmentId, t.CreatedAtUtc, t.FirstHumanResponseAtUtc))
            .ToListAsync(cancellationToken);
        return await BuildAsync(tickets, cancellationToken);
    }

    private sealed record TicketRow(
        long TicketId, string TicketNumber, TicketStatus Status, Guid? Owner, int CurrentDepartmentId, int OriginatingDepartmentId,
        DateTime CreatedAtUtc, DateTime? FirstHumanResponseAtUtc);

    private async Task<IReadOnlyList<UnassignedTicketEvidence>> BuildAsync(List<TicketRow> tickets, CancellationToken ct)
    {
        if (tickets.Count == 0)
        {
            return [];
        }

        var ids = tickets.Select(t => t.TicketId).ToList();
        var idText = ids.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();

        var callCenterId = await db.Departments.AsNoTracking()
            .Where(d => d.Code == TeamPerformanceAppService.CallCenterDepartmentCode)
            .Select(d => (int?)d.DepartmentId).FirstOrDefaultAsync(ct);

        var pendingApproval = (await db.TicketApprovals.AsNoTracking()
            .Where(a => ids.Contains(a.TicketId) && a.Status == ApprovalStatus.Pending)
            .Select(a => a.TicketId).Distinct().ToListAsync(ct)).ToHashSet();

        var interactions = (await db.TicketInteractions.AsNoTracking()
            .Where(i => ids.Contains(i.TicketId))
            .Select(i => new
            {
                i.TicketId, i.TicketInteractionId, i.Source, i.ChannelId, i.EndedAtUtc, i.InactivityClosedAtUtc, i.RequiresHumanFollowUp,
                i.HandledByUserId, i.GenesysAgentId, i.GenesysAgentUserId, i.CreatedAtUtc, i.InteractionStartedAtUtc,
                i.LastCustomerInteractionAtUtc, i.AwaitingCustomerReplySinceUtc,
            }).ToListAsync(ct)).ToLookup(i => i.TicketId);

        var handlers = (await db.TicketInteractionHandlers.AsNoTracking()
            .Where(h => ids.Contains(h.TicketId))
            .Select(h => new { h.TicketId, h.HandledByUserId, h.GenesysAgentUserId, h.ConnectedAtUtc, h.RecordedAtUtc })
            .ToListAsync(ct)).ToLookup(h => h.TicketId);

        var openHandoffs = (await db.TicketAgentHandoffs.AsNoTracking()
            .Where(h => ids.Contains(h.TicketId) && (h.Status == AgentHandoffStatus.WaitingForAgent || h.Status == AgentHandoffStatus.Assigned || h.Status == AgentHandoffStatus.InProgress))
            .Select(h => h.TicketId).Distinct().ToListAsync(ct)).ToHashSet();

        var messages = (await (from m in db.TicketInteractionMessages.AsNoTracking()
                               join i in db.TicketInteractions.AsNoTracking() on m.TicketInteractionId equals i.TicketInteractionId
                               where ids.Contains(i.TicketId)
                               select new { i.TicketId, m.Sender, m.SenderId, m.SentAtUtc }).ToListAsync(ct)).ToLookup(m => m.TicketId);

        // A HumanAgent line carries the writer's Genesys user id: resolve it to the Ticketing user, so the person who ACTUALLY wrote to the customer
        // is known even when no handler row was recorded.
        var humanSenderIds = messages.SelectMany(g => g).Where(m => m.Sender == InteractionMessageSender.HumanAgent && !string.IsNullOrWhiteSpace(m.SenderId))
            .Select(m => m.SenderId!.Trim()).Distinct().ToList();
        var senderUsers = humanSenderIds.Count == 0
            ? new Dictionary<string, Guid>()
            : (await db.Users.AsNoTracking().Where(u => u.GenesysUserId != null && humanSenderIds.Contains(u.GenesysUserId)).Select(u => new { u.GenesysUserId, u.Id }).ToListAsync(ct))
                .ToDictionary(u => u.GenesysUserId!, u => u.Id);

        var events = (await db.TicketWorkflowEvents.AsNoTracking()
            .Where(e => ids.Contains(e.TicketId))
            .Select(e => new { e.TicketId, e.EventType, e.OccurredAtUtc, e.FromDepartmentId })
            .ToListAsync(ct)).ToLookup(e => e.TicketId);

        var released = (await db.AuditEntries.AsNoTracking()
            .Where(a => a.Action == "Unassign" && a.EntityType == "Ticket" && a.ActorEmployeeId != null && idText.Contains(a.EntityId!))
            .Select(a => a.EntityId!).Distinct().ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        var result = new List<UnassignedTicketEvidence>(tickets.Count);
        foreach (var t in tickets)
        {
            var ti = interactions[t.TicketId].ToList();
            var genesys = ti.Where(i => i.Source == InteractionContextSource.Genesys).ToList();
            var live = genesys.Where(i => i.EndedAtUtc == null && i.InactivityClosedAtUtc == null).ToList();
            var th = handlers[t.TicketId].OrderByDescending(h => h.ConnectedAtUtc ?? h.RecordedAtUtc).ToList();
            var tm = messages[t.TicketId].ToList();
            var te = events[t.TicketId].ToList();

            // The agent who actually answered: the latest recorded handler, else the interaction's resolved handler.
            Guid? answeringId = th.FirstOrDefault()?.HandledByUserId
                ?? ti.Where(i => i.HandledByUserId != null).Select(i => i.HandledByUserId).FirstOrDefault();
            var answeringGenesysId = th.FirstOrDefault()?.GenesysAgentUserId
                ?? ti.Where(i => i.HandledByUserId != null).Select(i => i.GenesysAgentUserId).FirstOrDefault();

            // No handler row: the first mapped person who wrote a HumanAgent line received the conversation.
            if (answeringId is null
                && tm.Where(m => m.Sender == InteractionMessageSender.HumanAgent && !string.IsNullOrWhiteSpace(m.SenderId))
                     .OrderBy(m => m.SentAtUtc)
                     .Select(m => m.SenderId!.Trim())
                     .FirstOrDefault(id => senderUsers.ContainsKey(id)) is { } writerGenesysId)
            {
                answeringId = senderUsers[writerGenesysId];
                answeringGenesysId = writerGenesysId;
            }

            var phoneUnmapped = answeringId is null
                && genesys.Any(i => i.ChannelId == WellKnownChannels.Phone && !string.IsNullOrWhiteSpace(i.GenesysAgentId) && i.HandledByUserId == null);
            var unmappedId = phoneUnmapped
                ? genesys.First(i => i.ChannelId == WellKnownChannels.Phone && !string.IsNullOrWhiteSpace(i.GenesysAgentId) && i.HandledByUserId == null).GenesysAgentId
                : null;

            var lastActivity = new[]
            {
                t.CreatedAtUtc,
                ti.Select(i => new[] { i.CreatedAtUtc, i.InteractionStartedAtUtc, i.LastCustomerInteractionAtUtc, i.AwaitingCustomerReplySinceUtc }.Max() ?? i.CreatedAtUtc).DefaultIfEmpty(t.CreatedAtUtc).Max(),
                tm.Select(m => m.SentAtUtc).DefaultIfEmpty(t.CreatedAtUtc).Max(),
                te.Where(e => e.EventType >= WorkflowEventType.HandoffRequested && e.EventType <= WorkflowEventType.GenesysCallTransferred)
                  .Select(e => e.OccurredAtUtc).DefaultIfEmpty(t.CreatedAtUtc).Max(),
            }.Max();

            var left = callCenterId is { } cc
                && (te.Any(e => e.EventType == WorkflowEventType.TicketTransferred && e.FromDepartmentId == cc)
                    || (t.OriginatingDepartmentId == cc && t.CurrentDepartmentId != cc));

            result.Add(new UnassignedTicketEvidence(
                t.TicketId, t.TicketNumber, t.Status, t.Owner, t.CurrentDepartmentId, t.OriginatingDepartmentId, t.CreatedAtUtc,
                PendingApproval: pendingApproval.Contains(t.TicketId),
                HasGenesysInteraction: genesys.Count > 0,
                HasLiveGenesysInteraction: live.Count > 0,
                RequiresHumanFollowUp: ti.Any(i => i.RequiresHumanFollowUp),
                OpenHandoff: openHandoffs.Contains(t.TicketId),
                HumanEvidence: t.FirstHumanResponseAtUtc != null
                    || tm.Any(m => m.Sender == InteractionMessageSender.HumanAgent)
                    || te.Any(e => HumanEventTypes.Contains(e.EventType)),
                AnsweringEmployeeId: answeringId,
                AnsweringGenesysUserId: answeringGenesysId,
                PhonePickupByUnmappedAgent: phoneUnmapped,
                UnmappedGenesysAgentId: unmappedId,
                LastActivityUtc: lastActivity,
                LeftCallCenter: left,
                ReleasedByPerson: released.Contains(t.TicketId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                LiveGenesysIsPhoneOnly: live.Count > 0 && live.All(i => i.ChannelId == WellKnownChannels.Phone)));
        }

        return result;
    }
}
