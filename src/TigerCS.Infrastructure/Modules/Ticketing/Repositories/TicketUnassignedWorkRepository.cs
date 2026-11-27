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
            // A queue-managed ticket waits unassigned by design: the Queueing System, not a reconciler, names who serves it.
            .Where(t => t.TicketId > afterTicketId && t.TicketStatus == TicketStatus.Open && t.CurrentOwnerEmployeeId == null
                && (t.IntegrationSource == null || t.IntegrationSource != TicketIntegrationSources.QueueingSystem))
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
                && t.TicketStatus != TicketStatus.Resolved && t.TicketStatus != TicketStatus.Closed
                && (t.IntegrationSource == null || t.IntegrationSource != TicketIntegrationSources.QueueingSystem))
            .OrderBy(t => t.TicketId)
            .Take(limit)
            .Select(t => new TicketRow(t.TicketId, t.TicketNumber, t.TicketStatus, t.CurrentOwnerEmployeeId, t.CurrentDepartmentId, t.OriginatingDepartmentId, t.CreatedAtUtc, t.FirstHumanResponseAtUtc))
            .ToListAsync(cancellationToken);
        return await BuildAsync(tickets, cancellationToken);
    }

    public async Task<IReadOnlyList<UnassignedTicketEvidence>> ListVoiceCandidatesAsync(DateTime endedSinceUtc, long afterTicketId, int limit, CancellationToken cancellationToken = default)
    {
        // Narrow on purpose (the voice reconciler runs every minute): an Open, unowned ticket with a Genesys PHONE interaction that ended inside the look-back window,
        // or whose answering agent was flagged (a corrected mapping must be noticed however old the call is), or that is KNOWN to need a person (RequiresHumanFollowUp:
        // Genesys asked for a human or an earlier run concluded "nobody answered") and still has no owner - however old, so a ticket stuck on the AI Agent is recovered
        // and retried until an eligible Call Center agent exists. Everything else is never read.
        var tickets = await db.Tickets.AsNoTracking()
            .Where(t => t.TicketId > afterTicketId && t.TicketStatus == TicketStatus.Open && t.CurrentOwnerEmployeeId == null
                && (t.IntegrationSource == null || t.IntegrationSource != TicketIntegrationSources.QueueingSystem)
                && db.TicketInteractions.Any(i => i.TicketId == t.TicketId && i.Source == InteractionContextSource.Genesys && i.ChannelId == WellKnownChannels.Phone
                    && ((i.EndedAtUtc != null && (i.EndReceivedAtUtc ?? i.EndedAtUtc) >= endedSinceUtc)
                        || (i.EndedAtUtc != null && i.RequiresHumanFollowUp)
                        || db.TicketWorkflowEvents.Any(e => e.TicketId == t.TicketId && e.EventType == WorkflowEventType.ManualAssignmentRequired))))
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
                i.HandledByUserId, i.GenesysAgentId, i.GenesysAgentUserId, i.CreatedAtUtc, i.InteractionStartedAtUtc, i.EndReceivedAtUtc,
                i.LastCustomerInteractionAtUtc, i.AwaitingCustomerReplySinceUtc,
            }).ToListAsync(ct)).ToLookup(i => i.TicketId);

        var handlers = (await db.TicketInteractionHandlers.AsNoTracking()
            .Where(h => ids.Contains(h.TicketId))
            .Select(h => new { h.TicketId, h.HandledByUserId, h.GenesysAgentUserId, h.ConnectedAtUtc, h.RecordedAtUtc, h.Source })
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
            .Select(e => new { e.TicketId, e.EventType, e.OccurredAtUtc, e.FromDepartmentId, e.ToDepartmentId, e.CorrelationId, e.Note })
            .ToListAsync(ct)).ToLookup(e => e.TicketId);

        // Transfers whose automation ran and found nobody: the "AutoAssign ... DepartmentQueue ... Trigger=DepartmentTransfer" audit entry shares the
        // transfer event's correlation id. A ticket merely sitting in a department queue (every legacy / pre-existing one) has no such entry, so it is
        // never picked up by the department rule - only a transfer that actually tried and failed is.
        var failedTransferAutomation = (await db.AuditEntries.AsNoTracking()
            .Where(a => a.Action == "AutoAssign" && a.EntityType == "Ticket" && idText.Contains(a.EntityId!)
                && a.AfterValue != null && a.AfterValue.StartsWith("DepartmentQueue") && a.AfterValue.Contains("Trigger=DepartmentTransfer"))
            .Select(a => a.CorrelationId).Distinct().ToListAsync(ct)).ToHashSet();

        // Latest assignment row per ticket (any, current or superseded): "nobody has been assigned since the transfer" is judged against it.
        var lastAssignedAt = (await db.TicketAssignments.AsNoTracking()
            .Where(a => ids.Contains(a.TicketId))
            .GroupBy(a => a.TicketId)
            .Select(g => new { TicketId = g.Key, At = g.Max(a => a.AssignedAtUtc) })
            .ToListAsync(ct)).ToDictionary(x => x.TicketId, x => x.At);

        var released = (await db.AuditEntries.AsNoTracking()
            .Where(a => a.Action == "Unassign" && a.EntityType == "Ticket" && a.ActorEmployeeId != null && idText.Contains(a.EntityId!))
            .Select(a => a.EntityId!).Distinct().ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        // VOICE: the Genesys ids of agents whose answer was FLAGGED (ManualAssignmentRequired: unmapped / ineligible), resolved against the CURRENT mapping so a
        // corrected mapping is noticed. The marker text is written by TicketAutoAssignmentService ("[GenesysUserId=<id>;Issue=<issue>;ConnectedAtUtc=<o>]").
        var flaggedIds = events.SelectMany(g => g)
            .Where(e => e.EventType == WorkflowEventType.ManualAssignmentRequired)
            .Select(e => FlaggedAnswer.TryParse(e.Note))
            .Where(f => f is not null).Select(f => f!.GenesysUserId).Distinct().ToList();
        var flaggedUsers = flaggedIds.Count == 0
            ? new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
            : (await db.Users.AsNoTracking().Where(u => u.GenesysUserId != null && flaggedIds.Contains(u.GenesysUserId)).Select(u => new { u.GenesysUserId, u.Id }).ToListAsync(ct))
                .ToDictionary(u => u.GenesysUserId!, u => u.Id, StringComparer.OrdinalIgnoreCase);

        // VOICE: the agents Genesys OFFERED a call to (GenesysAgentOffered events), resolved against the CURRENT mapping.
        var offeredIds = events.SelectMany(g => g)
            .Where(e => e.EventType == WorkflowEventType.GenesysAgentOffered)
            .Select(e => OfferedAgent.TryParse(e.Note)?.GenesysUserId)
            .Where(id => id is not null).Select(id => id!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var offeredUsers = offeredIds.Count == 0
            ? new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
            : (await db.Users.AsNoTracking().Where(u => u.GenesysUserId != null && offeredIds.Contains(u.GenesysUserId)).Select(u => new { u.GenesysUserId, u.Id }).ToListAsync(ct))
                .GroupBy(u => u.GenesysUserId!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1)   // an ambiguous id maps to nobody
                .ToDictionary(g => g.Key, g => g.Single().Id, StringComparer.OrdinalIgnoreCase);

        var result = new List<UnassignedTicketEvidence>(tickets.Count);
        foreach (var t in tickets)
        {
            var ti = interactions[t.TicketId].ToList();
            var genesys = ti.Where(i => i.Source == InteractionContextSource.Genesys).ToList();
            var live = genesys.Where(i => i.EndedAtUtc == null && i.InactivityClosedAtUtc == null).ToList();
            var phoneOnly = genesys.Count > 0 && genesys.All(i => i.ChannelId == WellKnownChannels.Phone);

            // VOICE: only CONFIRMED human connection counts as an answer - a pickup recorded at creation, a routing update that named the CONNECTED agent, or a person who wrote
            // to the customer. A handler recorded from a top-level agentId (source Update / ConversationEnd) can be an agent who was only offered / assigned / ringing, so an
            // agent id alone is never evidence. Digital tickets keep every recorded handler (unchanged).
            var th = handlers[t.TicketId]
                .Where(h => !phoneOnly || h.Source is InteractionHandlerSource.Inquiry or InteractionHandlerSource.Routing or InteractionHandlerSource.Transcript or InteractionHandlerSource.Legacy)
                .OrderByDescending(h => h.ConnectedAtUtc ?? h.RecordedAtUtc).ToList();
            var tm = messages[t.TicketId].ToList();
            var te = events[t.TicketId].ToList();

            // The agent who actually answered: the latest recorded handler, else the interaction's resolved handler.
            Guid? answeringId = th.FirstOrDefault()?.HandledByUserId
                ?? (phoneOnly ? null : ti.Where(i => i.HandledByUserId != null).Select(i => i.HandledByUserId).FirstOrDefault());
            var answeringGenesysId = th.FirstOrDefault()?.GenesysAgentUserId
                ?? (phoneOnly ? null : ti.Where(i => i.HandledByUserId != null).Select(i => i.GenesysAgentUserId).FirstOrDefault());

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

            // A transfer INTO the current department, with no assignment made at or after it: the owner was released (or never existed) and nobody
            // eligible took it. A person's later release, a manual assignment or a reconciled assignment all create a row at/after the transfer.
            var lastTransferIn = te.Where(e => e.EventType == WorkflowEventType.TicketTransferred && e.ToDepartmentId == t.CurrentDepartmentId)
                .OrderByDescending(e => e.OccurredAtUtc).FirstOrDefault();
            var awaitingDepartmentAssignment = lastTransferIn is not null
                && failedTransferAutomation.Contains(lastTransferIn.CorrelationId)
                && (!lastAssignedAt.TryGetValue(t.TicketId, out var assignedAt) || assignedAt < lastTransferIn.OccurredAtUtc);

            DateTime? voiceEndReceived = phoneOnly && live.Count == 0
                ? genesys.Max(i => i.EndReceivedAtUtc ?? i.EndedAtUtc ?? i.InactivityClosedAtUtc)
                : null;

            // The latest flagged answer (a later one supersedes an earlier flag for the same ticket).
            var flagged = te.Where(e => e.EventType == WorkflowEventType.ManualAssignmentRequired)
                .OrderByDescending(e => e.OccurredAtUtc)
                .Select(e => (Parsed: FlaggedAnswer.TryParse(e.Note), e.OccurredAtUtc))
                .FirstOrDefault(x => x.Parsed is not null);

            // A person ANSWERED but cannot be given the ticket = a CONNECTED agent (a connect event / a phone pickup) whose id maps to nobody or to an ineligible account.
            // That is recorded by the flag the answering-agent rule writes (ManualAssignmentRequired, with the Genesys id). The agent id stored on the interaction is NOT
            // evidence: RecordAgentIfAbsent also stores an agent that was only offered / assigned / ringing, who may never have picked up.
            // VOICE: the LAST agent the call was offered to - the latest offer by the time Genesys gave (the receipt time when it gave none), then by arrival.
            var lastOffer = phoneOnly
                ? te.Where(e => e.EventType == WorkflowEventType.GenesysAgentOffered)
                    .Select(e => (Parsed: OfferedAgent.TryParse(e.Note), e.OccurredAtUtc))
                    .Where(x => x.Parsed is not null)
                    .OrderBy(x => x.Parsed!.OfferedAtUtc ?? x.OccurredAtUtc).ThenBy(x => x.OccurredAtUtc)
                    .LastOrDefault()
                : default;
            var lastOfferedId = lastOffer.Parsed?.GenesysUserId;

            var phoneUnmapped = answeringId is null && flagged.Parsed is not null;
            var unmappedId = phoneUnmapped ? flagged.Parsed!.GenesysUserId : null;

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
                LiveGenesysIsPhoneOnly: live.Count > 0 && live.All(i => i.ChannelId == WellKnownChannels.Phone),
                AwaitingDepartmentAssignment: awaitingDepartmentAssignment,
                GenesysIsPhoneOnly: phoneOnly,
                VoiceEndReceivedAtUtc: voiceEndReceived,
                FlaggedAnsweringGenesysUserId: flagged.Parsed?.GenesysUserId,
                FlaggedAnsweringConnectedAtUtc: flagged.Parsed?.ConnectedAtUtc,
                HumanAnswered: t.FirstHumanResponseAtUtc != null
                    || answeringId is not null
                    || (!phoneOnly && ti.Any(i => i.HandledByUserId != null))
                    || tm.Any(m => m.Sender == InteractionMessageSender.HumanAgent)
                    || te.Any(e => e.EventType == WorkflowEventType.HandoffStarted
                        || (!phoneOnly && e.EventType is WorkflowEventType.GenesysAgentConnected or WorkflowEventType.GenesysCallTransferred))
                    || flagged.Parsed is not null,
                AnsweringConnectedAtUtc: th.FirstOrDefault()?.ConnectedAtUtc,
                FlaggedAnsweringEmployeeId: flagged.Parsed is { } fp && flaggedUsers.TryGetValue(fp.GenesysUserId, out var flaggedEmployee) ? flaggedEmployee : null,
                VoiceLastOfferedGenesysUserId: lastOfferedId,
                VoiceLastOfferedEmployeeId: lastOfferedId is not null && offeredUsers.TryGetValue(lastOfferedId, out var offeredEmployee) ? offeredEmployee : null,
                VoiceLastOfferedAtUtc: lastOffer.Parsed is null ? null : lastOffer.Parsed.OfferedAtUtc ?? lastOffer.OccurredAtUtc));
        }

        return result;
    }
}
