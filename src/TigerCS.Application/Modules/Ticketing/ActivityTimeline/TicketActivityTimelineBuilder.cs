using System.Globalization;
using TigerCS.Domain.Audit;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.ActivityTimeline;

/// <summary>Audit actions the timeline reads. Named once; the first three are written by the lifecycle service on every system closure.</summary>
public static class TimelineAuditActions
{
    public const string AutoCloseCustomerInactivity = "AutoCloseCustomerInactivity";
    public const string AutoCloseByAi = "AutoCloseByAi";
    public const string AutoCloseDocumentDelivery = "AutoCloseDocumentDelivery";

    /// <summary>Written (forward-compatible, from this release on) when a human reply reaches TigerCS for a ticket that is already Closed. Its audit time is the moment TigerCS received it.</summary>
    public const string HumanActivityAfterClosure = "TicketHumanActivityAfterClosure";

    /// <summary>Every audit action the timeline reads.</summary>
    public static readonly IReadOnlyCollection<string> All =
    [
        AutoCloseCustomerInactivity, AutoCloseByAi, AutoCloseDocumentDelivery, HumanActivityAfterClosure,
        "GenesysRoutingChanged", "GenesysTranscriptAppended", "GenesysHumanFollowUpRequired",
        "GenesysHandoffAutoRaisedOnConversationEnd", "GenesysConversationEnded"
    ];
}

/// <summary>Everything the builder reads, already loaded. Pure data: the builder performs no I/O, so every scenario is testable without a database.</summary>
public sealed class TicketTimelineSources
{
    public required long TicketId { get; init; }
    public required string TicketNumber { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public Guid? CreatedByEmployeeId { get; init; }

    public IReadOnlyList<TicketInteraction> Interactions { get; init; } = [];

    /// <summary>The ticket's OWN interactions' messages only (a sibling ticket's conversation does not make this ticket human-handled).</summary>
    public IReadOnlyDictionary<long, IReadOnlyList<TicketInteractionMessage>> MessagesByInteraction { get; init; } =
        new Dictionary<long, IReadOnlyList<TicketInteractionMessage>>();

    public IReadOnlyDictionary<byte, string> ChannelNames { get; init; } = new Dictionary<byte, string>();
    public IReadOnlyList<TicketAgentHandoff> Handoffs { get; init; } = [];
    public IReadOnlyList<TicketInteractionHandler> Handlers { get; init; } = [];
    public IReadOnlyList<TicketWorkflowEvent> WorkflowEvents { get; init; } = [];
    public IReadOnlyList<TicketStatusHistory> StatusHistory { get; init; } = [];
    public IReadOnlyList<TicketAssignment> Assignments { get; init; } = [];
    public IReadOnlyList<AuditEntry> AuditEntries { get; init; } = [];
    public IReadOnlyDictionary<Guid, string> EmployeeNames { get; init; } = new Dictionary<Guid, string>();
    public IReadOnlyDictionary<int, string> DepartmentNames { get; init; } = new Dictionary<int, string>();

    /// <summary>Employees who hold a supervisory role — a human closure by one of them reads "Supervisor". Empty when roles were not read.</summary>
    public IReadOnlySet<Guid> SupervisorIds { get; init; } = new HashSet<Guid>();
}

/// <summary>
/// Builds the ticket's chronological Activity timeline from the stores that already hold the facts: TicketInteractions, the transcript,
/// agent handoffs, interaction handlers, workflow events, lifecycle history, ownership history and the audit trail.
///
/// <para>
/// <b>Evidence only.</b> A timestamp, actor or reason that no store holds is shown as <see cref="TimelineWording.Unknown"/> or
/// <see cref="TimelineWording.NotRecorded"/> and listed in <see cref="TicketActivityTimelineDto.EvidenceGaps"/>; nothing is inferred
/// silently. Where an actor is derived (for example the end-reason of a conversation), the description says so.
/// </para>
///
/// <para>
/// <b>Milestones, not messages.</b> The transcript contributes a handful of milestones (conversation start, first/last AI reply, last
/// customer message, first/latest human reply); each links to its interaction so the reader opens the full Conversation History for the rest.
/// </para>
/// </summary>
public static class TicketActivityTimelineBuilder
{
    /// <summary>A recorded time this much later than the event's own time is surfaced as a delayed / out-of-order delivery.</summary>
    public static readonly TimeSpan DelayTolerance = TimeSpan.FromMinutes(2);

    /// <summary>A transfer and the assignment it caused are written in one transaction; timestamps this close are the same act.</summary>
    private static readonly TimeSpan SameActTolerance = TimeSpan.FromSeconds(5);

    /// <summary>A human reply this close before a closure counts as "still responding" at closure time.</summary>
    private static readonly TimeSpan RespondingWindow = TimeSpan.FromMinutes(10);

    private const string SourceHistory = "TicketStatusHistory";
    private const string SourceInteraction = "TicketInteractions";
    private const string SourceTranscript = "TicketInteractionMessages";
    private const string SourceHandoff = "TicketAgentHandoffs";
    private const string SourceHandler = "TicketInteractionHandlers";
    private const string SourceWorkflow = "TicketWorkflowEvents";
    private const string SourceAssignment = "TicketAssignments";
    private const string SourceAudit = "AuditEntries";

    private sealed record Entry(TicketActivityEventDto Event, int Rank);

    public static TicketActivityTimelineDto Build(TicketTimelineSources s)
    {
        ArgumentNullException.ThrowIfNull(s);

        var entries = new List<Entry>();
        var gaps = new List<string>();
        var ctx = new Context(s);

        AddCreated(s, ctx, entries, gaps);
        AddConversationMilestones(s, ctx, entries, gaps);
        AddHandoffs(s, ctx, entries);
        AddHandlers(s, ctx, entries, gaps);
        AddAuditMilestones(s, ctx, entries);
        AddOwnership(s, ctx, entries);
        AddLifecycle(s, ctx, entries);
        var closures = AddClosures(s, ctx, entries, gaps);

        var ordered = entries
            .Select((e, index) => (e, index))
            .OrderBy(x => x.e.Event.OccurredAtUtc)
            .ThenBy(x => x.e.Rank)
            .ThenBy(x => x.index)
            .Select(x => x.e.Event)
            .ToList();

        return new TicketActivityTimelineDto(s.TicketId, s.TicketNumber, ordered, closures, gaps.Distinct().ToList());
    }

    // ------------------------------------------------------------------ context

    private sealed class Context
    {
        public Context(TicketTimelineSources s)
        {
            Sources = s;
            Ordinal = s.Interactions
                .OrderBy(i => i.InteractionStartedAtUtc ?? i.CreatedAtUtc).ThenBy(i => i.TicketInteractionId)
                .Select((i, index) => (i.TicketInteractionId, index + 1))
                .ToDictionary(x => x.TicketInteractionId, x => x.Item2);
        }

        public TicketTimelineSources Sources { get; }
        public Dictionary<long, int> Ordinal { get; }

        public string Name(Guid? id)
        {
            if (id is not { } known)
            {
                return TimelineWording.NotRecorded;
            }

            return Sources.EmployeeNames.TryGetValue(known, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name
                : $"{TimelineWording.Unknown} employee ({known.ToString()[..8]})";
        }

        public string Department(int? id) =>
            id is { } known
                ? Sources.DepartmentNames.TryGetValue(known, out var name) ? name : $"{TimelineWording.Unknown} department (#{known})"
                : TimelineWording.NotRecorded;

        public string Channel(byte channelId) =>
            Sources.ChannelNames.TryGetValue(channelId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : $"Channel #{channelId}";

        public string InteractionLabel(TicketInteraction interaction)
        {
            var many = Sources.Interactions.Count > 1;
            var channel = Channel(interaction.ChannelId);
            return many && Ordinal.TryGetValue(interaction.TicketInteractionId, out var n)
                ? $"interaction {n} of {Sources.Interactions.Count} ({channel})"
                : $"the {channel} conversation";
        }

        public IReadOnlyList<TicketInteractionMessage> Messages(long interactionId) =>
            Sources.MessagesByInteraction.TryGetValue(interactionId, out var m) ? m : [];

        public string ActorTypeFor(Guid? employeeId) =>
            employeeId is { } id && Sources.SupervisorIds.Contains(id) ? TimelineActorTypes.Supervisor : TimelineActorTypes.Agent;
    }

    private static string ActorLabel(Context ctx, Guid? employeeId) => ctx.Name(employeeId);

    private static Entry Make(
        DateTime occurredAtUtc, string eventType, string eventName, string category, string actorType, string actorName,
        string description, string source, int rank = 5, string? reason = null, string? previous = null, string? next = null,
        DateTime? recordedAtUtc = null, string? timeNote = null, long? interactionId = null, string severity = "Info",
        TicketClosureDetailsDto? closure = null)
    {
        // A recorded time is only worth showing when it differs materially from the event's own time.
        DateTime? recorded = recordedAtUtc is { } r && r - occurredAtUtc > DelayTolerance ? r : null;
        return new Entry(
            new TicketActivityEventDto(
                occurredAtUtc, recorded, eventType, eventName, category, actorType, actorName, description,
                reason, previous, next, source, timeNote, interactionId, severity, closure),
            rank);
    }

    // ------------------------------------------------------------------ creation

    private static void AddCreated(TicketTimelineSources s, Context ctx, List<Entry> entries, List<string> gaps)
    {
        var origin = s.Interactions.FirstOrDefault(i => i.IsOriginatingInteraction)
            ?? s.Interactions.OrderBy(i => i.CreatedAtUtc).FirstOrDefault();

        string source;
        string channel;
        string actorType;
        string actorName;
        if (origin is null)
        {
            source = TimelineWording.NotRecorded;
            channel = TimelineWording.NotRecorded;
            actorType = s.CreatedByEmployeeId is null ? TimelineActorTypes.Unknown : TimelineActorTypes.Agent;
            actorName = ctx.Name(s.CreatedByEmployeeId);
            gaps.Add("No interaction is recorded for this ticket, so its creation source and channel are not recorded.");
        }
        else
        {
            source = origin.Source == InteractionContextSource.Genesys ? "Genesys" : "TigerCS (entered by an employee)";
            channel = ctx.Channel(origin.ChannelId);
            if (origin.Source == InteractionContextSource.Genesys)
            {
                actorType = TimelineActorTypes.System;
                actorName = s.CreatedByEmployeeId is null
                    ? "Genesys integration"
                    : $"Genesys integration ({ctx.Name(s.CreatedByEmployeeId)})";
            }
            else
            {
                actorType = s.CreatedByEmployeeId is null ? TimelineActorTypes.Unknown : ctx.ActorTypeFor(s.CreatedByEmployeeId);
                actorName = ctx.Name(s.CreatedByEmployeeId);
            }
        }

        if (s.CreatedByEmployeeId is null)
        {
            gaps.Add("The ticket's creator is not recorded (created before creator tracking).");
        }

        entries.Add(Make(
            s.CreatedAtUtc, "TicketCreated", "Ticket created", "Lifecycle", actorType, actorName,
            $"Ticket {s.TicketNumber} created. Source: {source}. Channel: {channel}.",
            SourceInteraction, rank: 0, interactionId: origin?.TicketInteractionId));
    }

    // ------------------------------------------------------------------ conversations

    private static void AddConversationMilestones(TicketTimelineSources s, Context ctx, List<Entry> entries, List<string> gaps)
    {
        foreach (var interaction in s.Interactions.OrderBy(i => i.InteractionStartedAtUtc ?? i.CreatedAtUtc))
        {
            var id = interaction.TicketInteractionId;
            var label = ctx.InteractionLabel(interaction);
            var messages = ctx.Messages(id).OrderBy(m => m.Sequence).ToList();
            var aiMessages = messages.Where(m => m.Sender == InteractionMessageSender.VirtualAgent).ToList();
            var isGenesys = interaction.Source == InteractionContextSource.Genesys;

            // Conversation start. Genesys' own start time where supplied, otherwise the moment TigerCS recorded the interaction.
            var startedAt = interaction.InteractionStartedAtUtc ?? interaction.CreatedAtUtc;
            var startNote = interaction.InteractionStartedAtUtc is null
                ? "Genesys did not supply a start time; showing when TigerCS recorded the interaction."
                : null;
            var started = aiMessages.Count > 0;
            entries.Add(Make(
                startedAt,
                started ? "AiConversationStarted" : "ConversationStarted",
                started ? "AI conversation started" : "Conversation started",
                "Conversation",
                started ? TimelineActorTypes.Ai : TimelineActorTypes.Customer,
                started ? "AI virtual agent" : (interaction.CustomerName ?? "Customer"),
                $"{(isGenesys ? "Genesys" : "Entered in TigerCS")}: {label}{(interaction.Direction is { } d ? $", {d}" : string.Empty)}"
                + (interaction.GenesysQueueName is { } q ? $", queue {q}" : string.Empty) + ".",
                SourceInteraction, rank: 1, timeNote: startNote, interactionId: id));

            if (aiMessages.Count > 0)
            {
                var first = aiMessages[0];
                entries.Add(Make(
                    first.SentAtUtc, "AiFirstResponse", "First AI response", "Conversation", TimelineActorTypes.Ai,
                    first.SenderName ?? "AI virtual agent",
                    $"First AI reply in {label}. See Conversation History for the full transcript.",
                    SourceTranscript, interactionId: id));

                var last = aiMessages[^1];
                if (aiMessages.Count > 1 && last.SentAtUtc != first.SentAtUtc)
                {
                    entries.Add(Make(
                        last.SentAtUtc, "AiLastResponse", "Last AI response", "Conversation", TimelineActorTypes.Ai,
                        last.SenderName ?? "AI virtual agent",
                        $"Last of {aiMessages.Count} AI replies in {label}.",
                        SourceTranscript, interactionId: id));
                }
            }

            var customerMessages = messages.Where(m => m.Sender == InteractionMessageSender.Customer).ToList();
            if (customerMessages.Count > 0)
            {
                var last = customerMessages[^1];
                entries.Add(Make(
                    last.SentAtUtc, "CustomerLastMessage", "Last customer message", "Conversation", TimelineActorTypes.Customer,
                    last.SenderName ?? interaction.CustomerName ?? "Customer",
                    $"Last of {customerMessages.Count} customer message(s) in {label}.",
                    SourceTranscript, interactionId: id));
            }

            var humanMessages = messages.Where(m => m.Sender == InteractionMessageSender.HumanAgent).ToList();
            if (humanMessages.Count > 0)
            {
                var first = humanMessages[0];
                var received = ReceivedAt(s, interaction, first.Sequence);
                entries.Add(Make(
                    first.SentAtUtc, "FirstHumanResponse", "First human response", "Conversation", TimelineActorTypes.Agent,
                    first.SenderName ?? TimelineWording.NotRecorded,
                    $"First reply by a human agent in {label} ({humanMessages.Count} human message(s) in total). See Conversation History.",
                    SourceTranscript, recordedAtUtc: received, interactionId: id,
                    timeNote: received is { } r && r - first.SentAtUtc > DelayTolerance
                        ? "Delivered to TigerCS later than it was sent (delayed delivery)." : null));

                var last = humanMessages[^1];
                if (humanMessages.Count > 1 && last.SentAtUtc != first.SentAtUtc)
                {
                    var lastReceived = ReceivedAt(s, interaction, last.Sequence);
                    entries.Add(Make(
                        last.SentAtUtc, "LatestHumanResponse", "Latest human response", "Conversation", TimelineActorTypes.Agent,
                        last.SenderName ?? TimelineWording.NotRecorded,
                        $"Latest reply by a human agent in {label}.",
                        SourceTranscript, recordedAtUtc: lastReceived, interactionId: id));
                }
            }

            if (interaction.EndedAtUtc is { } endedAt)
            {
                var (actorType, actorName, derived) = EndActor(interaction, ctx);
                entries.Add(Make(
                    endedAt, "ConversationEnded", "Conversation ended", "Conversation", actorType, actorName,
                    $"{label[..1].ToUpperInvariant()}{label[1..]} ended."
                    + (derived ? " The actor is derived from the reported end reason." : string.Empty),
                    SourceInteraction, rank: 4, reason: interaction.EndReason ?? TimelineWording.NotRecorded, interactionId: id));
            }

            if (isGenesys && interaction.GenesysAgentId is not null && interaction.HandledByUserId is null)
            {
                gaps.Add(
                    $"Genesys agent '{interaction.GenesysAgentName ?? TimelineWording.NotRecorded}' ({interaction.GenesysAgentId}) is recorded on {label} " +
                    "but is not mapped to a TigerCS user, so no human-connected event can be shown for it.");
            }
        }
    }

    private static (string ActorType, string ActorName, bool Derived) EndActor(TicketInteraction interaction, Context ctx)
    {
        var reason = interaction.EndReason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            return (TimelineActorTypes.Unknown, TimelineWording.NotRecorded, false);
        }

        var r = reason.ToLowerInvariant();
        if (r.Contains("agent", StringComparison.Ordinal))
        {
            var name = interaction.HandledByUserId is { } handler ? ctx.Name(handler) : interaction.GenesysAgentName ?? TimelineWording.NotRecorded;
            return (TimelineActorTypes.Agent, name, true);
        }

        if (r.Contains("customer", StringComparison.Ordinal) || r.Contains("client", StringComparison.Ordinal))
        {
            return (TimelineActorTypes.Customer, interaction.CustomerName ?? "Customer", true);
        }

        if (r.Contains("timeout", StringComparison.Ordinal) || r.Contains("inactiv", StringComparison.Ordinal) || r.Contains("system", StringComparison.Ordinal))
        {
            return (TimelineActorTypes.System, "Genesys", true);
        }

        if (r.Contains("bot", StringComparison.Ordinal) || r.Contains("virtual", StringComparison.Ordinal) || r.Equals("ai", StringComparison.Ordinal))
        {
            return (TimelineActorTypes.Ai, "AI virtual agent", true);
        }

        return (TimelineActorTypes.Unknown, TimelineWording.Unknown, false);
    }

    /// <summary>
    /// When TigerCS received the transcript message with this sequence: the audit entry written when the batch that contained it was
    /// stored (its Messages/Appended counts give the sequence range it covered). Null when no entry covers it.
    /// </summary>
    private static DateTime? ReceivedAt(TicketTimelineSources s, TicketInteraction interaction, int sequence)
    {
        foreach (var audit in s.AuditEntries.Where(a => a.Action == "GenesysTranscriptAppended"))
        {
            var kv = ParseKv(audit.AfterValue);
            if (!BelongsToTicket(kv, s.TicketId)
                || audit.EntityId is null
                || !string.Equals(audit.EntityId, interaction.GenesysConversationId, StringComparison.Ordinal)
                || !TryInt(kv, "Messages", out var total)
                || !TryInt(kv, "Appended", out var appended))
            {
                continue;
            }

            if (sequence > total - appended && sequence <= total)
            {
                return audit.OccurredAtUtc;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ handoffs

    private static void AddHandoffs(TicketTimelineSources s, Context ctx, List<Entry> entries)
    {
        foreach (var handoff in s.Handoffs)
        {
            var interaction = s.Interactions.FirstOrDefault(i => i.TicketInteractionId == handoff.TicketInteractionId);
            var label = interaction is null ? "the conversation" : ctx.InteractionLabel(interaction);
            var id = handoff.TicketInteractionId;
            var reason = string.IsNullOrWhiteSpace(handoff.RequestReason) ? TimelineWording.NotRecorded : handoff.RequestReason;
            var mode = handoff.Mode?.ToString() ?? "mode not stated";

            var (requestName, requestActorType, requestActor) = handoff.Trigger switch
            {
                HandoffTrigger.CustomerRequestedHuman => ("Customer requested human assistance", TimelineActorTypes.Customer, interaction?.CustomerName ?? "Customer"),
                HandoffTrigger.AiEscalated => ("AI escalated to a human agent", TimelineActorTypes.Ai, "AI virtual agent"),
                HandoffTrigger.AiConnectionLost => ("AI connection lost — human requested", TimelineActorTypes.Ai, "AI virtual agent"),
                HandoffTrigger.RoutingDecision => ("Human assistance requested by routing rule", TimelineActorTypes.System, "Genesys routing"),
                HandoffTrigger.AgentTransfer => ("Agent transferred the conversation", TimelineActorTypes.Agent, TimelineWording.NotRecorded),
                _ => ("Human assistance requested", TimelineActorTypes.Unknown, TimelineWording.Unknown)
            };

            entries.Add(Make(
                handoff.RequestedAtUtc, "HumanAssistanceRequested", requestName, "Handoff", requestActorType, requestActor,
                $"Human help requested in {label} ({mode}). Trigger: {handoff.Trigger?.ToString() ?? "not stated"}.",
                SourceHandoff, rank: 2, reason: reason, interactionId: id));

            entries.Add(Make(
                handoff.RequestedAtUtc, "GenesysHandoffQueued", "Genesys handoff requested and queued", "Handoff", TimelineActorTypes.System, "Genesys",
                $"Pending human work raised for {ctx.Department(handoff.DepartmentId)}.",
                SourceHandoff, rank: 3, next: AgentHandoffStatus.WaitingForAgent.ToString(), interactionId: id));

            if (handoff.AssignedAtUtc is { } assignedAt)
            {
                var who = handoff.AssignedEmployeeId is { } employee
                    ? ctx.Name(employee)
                    : handoff.GenesysAgentId is { } genesysAgent
                        ? $"Genesys agent {genesysAgent} (not mapped to a TigerCS user)"
                        : TimelineWording.NotRecorded;
                entries.Add(Make(
                    assignedAt, "GenesysHandoffOffered", "Genesys offered / assigned to an agent", "Handoff", TimelineActorTypes.System, "Genesys",
                    $"Offered / assigned to {who}. Genesys does not report offer and assignment separately; this is the recorded assignment.",
                    SourceHandoff, rank: 3, previous: AgentHandoffStatus.WaitingForAgent.ToString(), next: AgentHandoffStatus.Assigned.ToString(), interactionId: id));
            }

            if (handoff.StartedAtUtc is { } startedAt)
            {
                entries.Add(Make(
                    startedAt, "GenesysHandoffAccepted", "Handoff accepted by agent", "Handoff", TimelineActorTypes.Agent,
                    ctx.Name(handoff.AssignedEmployeeId),
                    "The agent accepted the customer interaction.",
                    SourceHandoff, rank: 3, previous: AgentHandoffStatus.Assigned.ToString(), next: AgentHandoffStatus.InProgress.ToString(), interactionId: id));
            }

            if (handoff.CompletedAtUtc is { } completedAt)
            {
                entries.Add(Make(
                    completedAt, "HandoffCompleted", "Handoff completed", "Handoff", TimelineActorTypes.Agent,
                    ctx.Name(handoff.AssignedEmployeeId),
                    "The agent finished the customer interaction.",
                    SourceHandoff, rank: 3, reason: handoff.ResolutionNote, next: AgentHandoffStatus.Completed.ToString(), interactionId: id));
            }
            else if (handoff.Status == AgentHandoffStatus.Cancelled && handoff.ResolvedAtUtc is { } cancelledAt)
            {
                entries.Add(Make(
                    cancelledAt, "HandoffCancelled", "Handoff cancelled", "Handoff", TimelineActorTypes.System, TimelineWording.NotRecorded,
                    "The request for a human agent was stood down.",
                    SourceHandoff, rank: 3, reason: handoff.ResolutionNote ?? TimelineWording.NotRecorded, next: AgentHandoffStatus.Cancelled.ToString(), interactionId: id));
            }
        }

        // Older tickets may carry the typed workflow events without a handoff row; show them only then, so the story is never told twice.
        if (s.Handoffs.Count == 0)
        {
            foreach (var evt in s.WorkflowEvents.Where(e => e.EventType is >= WorkflowEventType.HandoffRequested and <= WorkflowEventType.HandoffCancelled))
            {
                entries.Add(Make(
                    evt.OccurredAtUtc, evt.EventType.ToString(), HandoffEventName(evt.EventType), "Handoff",
                    evt.ActorEmployeeId is null ? TimelineActorTypes.System : TimelineActorTypes.Agent,
                    evt.ActorEmployeeId is null ? "Genesys" : ctx.Name(evt.ActorEmployeeId),
                    "Recorded in the workflow event log (no handoff work item is stored for this ticket).",
                    SourceWorkflow, rank: 3, reason: evt.Note));
            }
        }
    }

    private static string HandoffEventName(WorkflowEventType type) => type switch
    {
        WorkflowEventType.HandoffRequested => "Human assistance requested",
        WorkflowEventType.HandoffAssigned => "Genesys offered / assigned to an agent",
        WorkflowEventType.HandoffStarted => "Handoff accepted by agent",
        WorkflowEventType.HandoffCompleted => "Handoff completed",
        _ => "Handoff cancelled"
    };

    // ------------------------------------------------------------------ human agents connected

    private static void AddHandlers(TicketTimelineSources s, Context ctx, List<Entry> entries, List<string> gaps)
    {
        foreach (var handler in s.Handlers)
        {
            var interaction = s.Interactions.FirstOrDefault(i => i.TicketInteractionId == handler.TicketInteractionId);
            var label = interaction is null ? "the conversation" : ctx.InteractionLabel(interaction);
            var occurred = handler.ConnectedAtUtc ?? handler.RecordedAtUtc;
            var note = handler.ConnectedAtUtc is null
                ? "Genesys did not supply a connect time; showing when TigerCS recorded the agent."
                : handler.RecordedAtUtc - handler.ConnectedAtUtc > DelayTolerance
                    ? "Recorded by TigerCS later than the agent connected (delayed delivery)."
                    : null;

            entries.Add(Make(
                occurred, "HumanAgentConnected", "Human agent connected", "Conversation", ctx.ActorTypeFor(handler.HandledByUserId),
                ctx.Name(handler.HandledByUserId),
                $"Connected to {label}. Genesys user ID {handler.GenesysAgentUserId} → TigerCS user {ctx.Name(handler.HandledByUserId)} (recorded via {handler.Source}).",
                SourceHandler, rank: 3, recordedAtUtc: handler.ConnectedAtUtc is null ? null : handler.RecordedAtUtc,
                timeNote: note, interactionId: handler.TicketInteractionId));
        }

        // Legacy: a handling agent recorded on the interaction without any handler row or timestamp. Not invented as an event.
        foreach (var interaction in s.Interactions.Where(i => i.HandledByUserId is not null && s.Handlers.All(h => h.TicketInteractionId != i.TicketInteractionId)))
        {
            var connected = s.WorkflowEvents.FirstOrDefault(e => e.EventType == WorkflowEventType.GenesysAgentConnected && e.ToEmployeeId == interaction.HandledByUserId);
            if (connected is not null)
            {
                entries.Add(Make(
                    connected.OccurredAtUtc, "HumanAgentConnected", "Human agent connected", "Conversation",
                    ctx.ActorTypeFor(interaction.HandledByUserId), ctx.Name(interaction.HandledByUserId),
                    $"First agent to handle {ctx.InteractionLabel(interaction)}. Genesys user ID {interaction.GenesysAgentUserId ?? TimelineWording.NotRecorded} → TigerCS user {ctx.Name(interaction.HandledByUserId)}.",
                    SourceWorkflow, rank: 3, interactionId: interaction.TicketInteractionId));
            }
            else
            {
                gaps.Add($"{ctx.Name(interaction.HandledByUserId)} is recorded as the handler of {ctx.InteractionLabel(interaction)} but no connection time is stored.");
            }
        }

        foreach (var evt in s.WorkflowEvents.Where(e => e.EventType == WorkflowEventType.GenesysCallTransferred))
        {
            entries.Add(Make(
                evt.OccurredAtUtc, "GenesysCallTransferred", "Conversation transferred between agents", "Conversation",
                TimelineActorTypes.Agent, evt.ActorEmployeeId is null ? "A Genesys agent" : ctx.Name(evt.ActorEmployeeId),
                $"Transferred to {ctx.Name(evt.ToEmployeeId)}. Handling history only — not an escalation.",
                SourceWorkflow, rank: 3, reason: evt.Note,
                previous: evt.ActorEmployeeId is null ? null : ctx.Name(evt.ActorEmployeeId), next: ctx.Name(evt.ToEmployeeId)));
        }
    }

    // ------------------------------------------------------------------ audit milestones

    private static void AddAuditMilestones(TicketTimelineSources s, Context ctx, List<Entry> entries)
    {
        foreach (var audit in s.AuditEntries)
        {
            var before = ParseKv(audit.BeforeValue);
            var after = ParseKv(audit.AfterValue);
            if (!BelongsToTicket(after, s.TicketId) || !BelongsToTicket(before, s.TicketId))
            {
                continue;
            }

            switch (audit.Action)
            {
                case "GenesysRoutingChanged":
                    var prevQueue = Pick(before, "QueueName");
                    var newQueue = Pick(after, "QueueName");
                    var prevAgent = Pick(before, "AgentName");
                    var newAgent = Pick(after, "AgentName");
                    entries.Add(Make(
                        audit.OccurredAtUtc, "ConversationRouted", "Conversation routing changed", "Routing", TimelineActorTypes.System, "Genesys",
                        "Genesys moved the conversation to another queue or agent.",
                        SourceAudit, rank: 3,
                        previous: $"Queue: {prevQueue}; Agent: {prevAgent}", next: $"Queue: {newQueue}; Agent: {newAgent}"));
                    break;

                case "GenesysHumanFollowUpRequired":
                    entries.Add(Make(
                        audit.OccurredAtUtc, "MarkedHumanWork", "Marked as human work", "Handoff", TimelineActorTypes.System, "Genesys",
                        "The ticket needs a person: AI auto-closure and inactivity closure no longer apply.",
                        SourceAudit, rank: 3, reason: $"Signal: {Pick(after, "HumanSignal")}"));
                    break;

                case "GenesysHandoffAutoRaisedOnConversationEnd":
                    entries.Add(Make(
                        audit.OccurredAtUtc, "HandoffAutoRaised", "Human follow-up raised automatically", "Handoff", TimelineActorTypes.System, "TigerCS",
                        "An AI conversation ended with nobody taking it, so TigerCS raised pending human work.",
                        SourceAudit, rank: 3));
                    break;

                case TimelineAuditActions.HumanActivityAfterClosure:
                    entries.Add(Make(
                        audit.OccurredAtUtc, "HumanActivityReceivedAfterClosure", "Human reply received after closure", "Anomaly",
                        TimelineActorTypes.Agent, Pick(after, "AgentName", TimelineWording.NotRecorded),
                        "A human agent's reply reached TigerCS while the ticket was already Closed. Recorded at the moment TigerCS received it.",
                        SourceAudit, rank: 8, severity: "Warning"));
                    break;
            }
        }
    }

    // ------------------------------------------------------------------ ownership & transfers

    private static void AddOwnership(TicketTimelineSources s, Context ctx, List<Entry> entries)
    {
        var transfers = s.WorkflowEvents.Where(e => e.EventType == WorkflowEventType.TicketTransferred).OrderBy(e => e.OccurredAtUtc).ToList();
        var assignments = s.Assignments.OrderBy(a => a.AssignedAtUtc).ThenBy(a => a.TicketAssignmentId).ToList();

        // Department transfers / reassignments / escalations — previous and receiving owner are both stated.
        foreach (var transfer in transfers)
        {
            var previousOwner = assignments
                .Where(a => a.AssignedAtUtc < transfer.OccurredAtUtc - SameActTolerance)
                .Select(a => a.AssignedEmployeeId)
                .Cast<Guid?>()
                .LastOrDefault();

            var movedDepartment = transfer.FromDepartmentId != transfer.ToDepartmentId;
            var isEscalation = transfer.IsEscalation;
            var released = transfer.ToEmployeeId is null;
            var name = isEscalation ? "Ticket escalated"
                : movedDepartment ? "Department transfer"
                : released ? "Ownership released"
                : "Ownership changed";

            var previous = movedDepartment
                ? $"{ctx.Department(transfer.FromDepartmentId)} / {OwnerLabel(ctx, previousOwner)}"
                : OwnerLabel(ctx, previousOwner);
            var next = movedDepartment
                ? $"{ctx.Department(transfer.ToDepartmentId)} / {(released ? "department queue (unassigned)" : ctx.Name(transfer.ToEmployeeId))}"
                : released ? "department queue (unassigned)" : ctx.Name(transfer.ToEmployeeId);

            entries.Add(Make(
                transfer.OccurredAtUtc, "TicketTransferred", name, "Ownership",
                transfer.ActorEmployeeId is null ? TimelineActorTypes.System : ctx.ActorTypeFor(transfer.ActorEmployeeId),
                transfer.ActorEmployeeId is null ? "System" : ctx.Name(transfer.ActorEmployeeId),
                $"{(isEscalation ? "Escalated" : movedDepartment ? "Transferred" : "Reassigned")} — previous owner derived from the ownership history.",
                SourceWorkflow, rank: 5, reason: JoinReason(transfer.TransferReasonCode is { } code ? Named<TicketTransferReason>(code) : null, transfer.Note),
                previous: previous, next: next));
        }

        Guid? previousAssignee = null;
        foreach (var assignment in assignments)
        {
            var coveredByTransfer = transfers.Any(t =>
                t.ToEmployeeId == assignment.AssignedEmployeeId
                && (t.OccurredAtUtc - assignment.AssignedAtUtc).Duration() <= SameActTolerance);

            if (!coveredByTransfer)
            {
                var first = previousAssignee is null;
                var actorType = assignment.AssigningActorEmployeeId is null ? TimelineActorTypes.System : ctx.ActorTypeFor(assignment.AssigningActorEmployeeId);
                var actor = assignment.AssigningActorEmployeeId is null
                    ? "System (automatic assignment)"
                    : ctx.Name(assignment.AssigningActorEmployeeId);

                entries.Add(Make(
                    assignment.AssignedAtUtc,
                    first ? "OwnerAssigned" : "OwnerChanged",
                    first ? "Ticket owner assigned" : "Ticket owner changed",
                    "Ownership", actorType, actor,
                    $"Assigned to {ctx.Name(assignment.AssignedEmployeeId)} in {ctx.Department(assignment.AssignedDepartmentId)}.",
                    SourceAssignment, rank: 5,
                    previous: first ? "Unassigned (department queue)" : ctx.Name(previousAssignee),
                    next: ctx.Name(assignment.AssignedEmployeeId)));
            }

            previousAssignee = assignment.AssignedEmployeeId;
        }
    }

    private static string OwnerLabel(Context ctx, Guid? owner) => owner is null ? "Unassigned (department queue)" : ctx.Name(owner);

    private static string? JoinReason(string? code, string? note) =>
        (code, note) switch
        {
            (null, null) or ("", "") => null,
            (_, null or "") => code,
            (null or "", _) => note,
            _ => $"{code}: {note}"
        };

    // ------------------------------------------------------------------ lifecycle

    private static void AddLifecycle(TicketTimelineSources s, Context ctx, List<Entry> entries)
    {
        foreach (var change in s.StatusHistory.OrderBy(h => h.OccurredAtUtc))
        {
            if (change.OldValue is null)
            {
                continue; // The seeding row written at creation; "Ticket created" covers it.
            }

            var actorType = change.ActorIsSystem || change.ActorEmployeeId is null ? TimelineActorTypes.System : ctx.ActorTypeFor(change.ActorEmployeeId);
            var actor = change.ActorIsSystem || change.ActorEmployeeId is null ? "System" : ctx.Name(change.ActorEmployeeId);

            switch (change.Dimension)
            {
                case TicketStatusDimension.TicketStatus:
                    var oldStatus = StatusName(change.OldValue);
                    var newStatus = StatusName(change.NewValue);
                    if (change.NewValue == (byte)TicketStatus.Closed)
                    {
                        break; // The closure analysis writes the richer "Ticket closed" event.
                    }

                    var reopened = change.OldValue == (byte)TicketStatus.Closed;
                    entries.Add(Make(
                        change.OccurredAtUtc,
                        reopened ? "TicketReopened" : $"Status{newStatus}",
                        reopened ? "Ticket reopened" : newStatus == nameof(TicketStatus.Resolved) ? "Ticket resolved" : $"Status changed to {newStatus}",
                        "Lifecycle", actorType, actor,
                        reopened ? "A closed ticket was reopened." : $"Status changed from {oldStatus} to {newStatus}.",
                        SourceHistory, rank: 6, reason: change.Note, previous: oldStatus, next: newStatus));
                    break;

                case TicketStatusDimension.ResolutionOutcome:
                    entries.Add(Make(
                        change.OccurredAtUtc, "ResolutionOutcomeRecorded", $"Resolution outcome: {Named<ResolutionOutcome>(change.NewValue)}",
                        "Lifecycle", actorType, actor, "The ticket's resolution outcome was recorded.",
                        SourceHistory, rank: 6, reason: change.Note, previous: change.OldValue is { } o ? Named<ResolutionOutcome>(o) : null,
                        next: Named<ResolutionOutcome>(change.NewValue)));
                    break;

                case TicketStatusDimension.SlaState:
                    entries.Add(Make(
                        change.OccurredAtUtc, "SlaStateChanged", "SLA state changed", "Sla", TimelineActorTypes.System, "System",
                        "The SLA state of the ticket changed.",
                        SourceHistory, rank: 6, reason: change.Note, previous: Named<TigerCS.Domain.Modules.Ticketing.SlaState>(change.OldValue.Value),
                        next: Named<TigerCS.Domain.Modules.Ticketing.SlaState>(change.NewValue)));
                    break;

                case TicketStatusDimension.VerificationStatus:
                    entries.Add(Make(
                        change.OccurredAtUtc, "VerificationChanged", "Customer verification changed", "Lifecycle", actorType, actor,
                        "The customer verification status changed.",
                        SourceHistory, rank: 6, reason: change.Note, previous: Named<CrmVerificationStatus>(change.OldValue.Value),
                        next: Named<CrmVerificationStatus>(change.NewValue)));
                    break;
            }
        }
    }

    private static string StatusName(byte? value) => value is { } v ? Named<TicketStatus>(v) : TimelineWording.NotRecorded;

    private static string Named<TEnum>(byte value) where TEnum : struct, Enum =>
        Enum.IsDefined(typeof(TEnum), value) ? Enum.ToObject(typeof(TEnum), value).ToString()! : value.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ closures

    private static List<TicketClosureDetailsDto> AddClosures(TicketTimelineSources s, Context ctx, List<Entry> entries, List<string> gaps)
    {
        var closures = new List<TicketClosureDetailsDto>();
        var history = s.StatusHistory.Where(h => h.Dimension == TicketStatusDimension.TicketStatus && h.OldValue is not null).OrderBy(h => h.OccurredAtUtc).ToList();

        foreach (var closing in history.Where(h => h.NewValue == (byte)TicketStatus.Closed))
        {
            var at = closing.OccurredAtUtc;
            var (type, name, reasonText, code, evidence) = Attribute(s, ctx, closing, history, gaps);
            var reopenedAt = history.Where(h => h.OldValue == (byte)TicketStatus.Closed && h.OccurredAtUtc > at).Select(h => (DateTime?)h.OccurredAtUtc).FirstOrDefault();

            var (ended, endedDetail) = InteractionEndedAt(s, ctx, at);
            var human = HumanAt(s, ctx, at);
            var after = HumanAfter(s, ctx, at, reopenedAt);

            var severity = "Info";
            if (human.Connected == "Yes" && type is ClosedByTypes.SystemAutomation or ClosedByTypes.Ai)
            {
                severity = "Warning";
            }

            var closure = new TicketClosureDetailsDto(
                at, type, name, reasonText, code, evidence, ended, endedDetail, human.Connected, human.Detail,
                human.LastActivity, after.Any, after.FirstSentAt, after.Assessment, after.Detail);

            if (after.Any || severity == "Warning")
            {
                severity = "Warning";
            }

            closures.Add(closure);
            entries.Add(Make(
                at, "TicketClosed", "Ticket closed", "Closure",
                type switch
                {
                    ClosedByTypes.Ai => TimelineActorTypes.Ai,
                    ClosedByTypes.SystemAutomation => TimelineActorTypes.System,
                    ClosedByTypes.Supervisor => TimelineActorTypes.Supervisor,
                    ClosedByTypes.HumanAgent => TimelineActorTypes.Agent,
                    _ => TimelineActorTypes.Unknown
                },
                name,
                $"Closed by {type}.",
                SourceHistory, rank: 7, reason: reasonText,
                previous: StatusName(closing.OldValue), next: nameof(TicketStatus.Closed), severity: severity, closure: closure));

            if (after is { Any: true, FirstSentAt: { } firstSent })
            {
                entries.Add(Make(
                    firstSent, "HumanActivityAfterClosure", "Human activity after closure", "Anomaly", TimelineActorTypes.Agent,
                    after.AgentName ?? TimelineWording.NotRecorded,
                    $"A human agent replied after the ticket was closed. Assessment: {after.Assessment}. {after.Detail}",
                    SourceTranscript, rank: 8, severity: "Warning", interactionId: after.InteractionId));
            }
        }

        return closures;
    }

    private static (string Type, string Name, string Reason, string Code, string Evidence) Attribute(
        TicketTimelineSources s, Context ctx, TicketStatusHistory closing, List<TicketStatusHistory> history, List<string> gaps)
    {
        var audit = s.AuditEntries.FirstOrDefault(a =>
            a.CorrelationId == closing.CorrelationId
            && a.Action is TimelineAuditActions.AutoCloseCustomerInactivity or TimelineAuditActions.AutoCloseByAi or TimelineAuditActions.AutoCloseDocumentDelivery);

        var note = string.IsNullOrWhiteSpace(closing.Note) ? null : closing.Note;

        if (audit is not null)
        {
            var reason = Pick(ParseKv(audit.AfterValue), "Reason", note ?? TimelineWording.NotRecorded);
            return audit.Action switch
            {
                TimelineAuditActions.AutoCloseCustomerInactivity => (ClosedByTypes.SystemAutomation, "Chatbot inactivity job", reason, "CustomerInactivity", "Audit entry"),
                TimelineAuditActions.AutoCloseByAi => (ClosedByTypes.Ai, "AI virtual agent", reason, "AiResolved", "Audit entry"),
                _ => (ClosedByTypes.SystemAutomation, "Document delivery workflow", reason, "DocumentDelivery", "Audit entry")
            };
        }

        if (closing.ActorIsSystem || closing.ActorEmployeeId is null)
        {
            // No audit entry matched. The lifecycle note is itself a recorded reason, so it may classify the closure — but the source is stated.
            if (note is not null && note.StartsWith("Automatically closed", StringComparison.OrdinalIgnoreCase) && note.Contains("inactiv", StringComparison.OrdinalIgnoreCase)
                || note is not null && note.Contains("did not respond", StringComparison.OrdinalIgnoreCase))
            {
                gaps.Add("A closure's audit entry is missing; its attribution comes from the lifecycle history note instead.");
                return (ClosedByTypes.SystemAutomation, "Chatbot inactivity job", note, "CustomerInactivity", "Lifecycle history note");
            }

            if (string.Equals(note, "Closed by AI", StringComparison.OrdinalIgnoreCase))
            {
                gaps.Add("A closure's audit entry is missing; its attribution comes from the lifecycle history note instead.");
                return (ClosedByTypes.Ai, "AI virtual agent", note!, "AiResolved", "Lifecycle history note");
            }

            gaps.Add("A system closure has no audit entry and no recognisable reason; its reason is not recorded.");
            return (ClosedByTypes.SystemAutomation, TimelineWording.NotRecorded, note ?? TimelineWording.NotRecorded, "NotRecorded", "Lifecycle history (system actor)");
        }

        var supervisor = s.SupervisorIds.Contains(closing.ActorEmployeeId.Value);
        var resolvedByAgent = history.Any(h =>
            h.NewValue == (byte)TicketStatus.Resolved && !h.ActorIsSystem && h.OccurredAtUtc <= closing.OccurredAtUtc
            && (h.CorrelationId == closing.CorrelationId || h.ActorEmployeeId == closing.ActorEmployeeId));

        return (
            supervisor ? ClosedByTypes.Supervisor : ClosedByTypes.HumanAgent,
            ctx.Name(closing.ActorEmployeeId),
            note ?? (resolvedByAgent ? "Resolved by agent" : TimelineWording.NotRecorded),
            resolvedByAgent ? "ResolvedByAgent" : "ManualClose",
            "Lifecycle history actor");
    }

    private static (string Ended, string Detail) InteractionEndedAt(TicketTimelineSources s, Context ctx, DateTime closedAt)
    {
        var genesys = s.Interactions.Where(i => i.Source == InteractionContextSource.Genesys && (i.InteractionStartedAtUtc ?? i.CreatedAtUtc) <= closedAt).ToList();
        if (genesys.Count == 0)
        {
            return ("Unknown", "No customer conversation is recorded for this ticket at closure time.");
        }

        var ended = genesys.Where(i => i.EndedAtUtc is { } e && e <= closedAt).ToList();
        if (ended.Count == genesys.Count)
        {
            return ("Yes", genesys.Count == 1
                ? $"The conversation had already ended (reason: {genesys[0].EndReason ?? TimelineWording.NotRecorded})."
                : $"All {genesys.Count} conversations had already ended.");
        }

        var live = genesys.Where(i => i.EndedAtUtc is null || i.EndedAtUtc > closedAt).ToList();
        return ("No", live.Count == 1
            ? $"{Capitalise(ctx.InteractionLabel(live[0]))} had not ended when the ticket was closed."
            : $"{live.Count} conversations had not ended when the ticket was closed.");
    }

    private sealed record HumanState(string Connected, string Detail, DateTime? LastActivity);

    private static HumanState HumanAt(TicketTimelineSources s, Context ctx, DateTime closedAt)
    {
        var names = new List<string>();
        DateTime? last = null;

        void See(DateTime at, string? name)
        {
            last = last is null || at > last ? at : last;
            if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        foreach (var handler in s.Handlers.Where(h => (h.ConnectedAtUtc ?? h.RecordedAtUtc) <= closedAt))
        {
            See(handler.ConnectedAtUtc ?? handler.RecordedAtUtc, ctx.Name(handler.HandledByUserId));
        }

        foreach (var message in HumanMessages(s).Where(m => m.Message.SentAtUtc <= closedAt))
        {
            See(message.Message.SentAtUtc, message.Message.SenderName);
        }

        foreach (var handoff in s.Handoffs)
        {
            if (handoff.StartedAtUtc is { } started && started <= closedAt)
            {
                See(started, ctx.Name(handoff.AssignedEmployeeId));
            }
            else if (handoff.AssignedAtUtc is { } assigned && assigned <= closedAt && handoff.Status != AgentHandoffStatus.Cancelled)
            {
                See(assigned, handoff.AssignedEmployeeId is null ? null : ctx.Name(handoff.AssignedEmployeeId));
            }
        }

        var owner = s.Assignments.Where(a => a.AssignedAtUtc <= closedAt).OrderBy(a => a.AssignedAtUtc).LastOrDefault();
        if (owner is not null)
        {
            names.Add($"{ctx.Name(owner.AssignedEmployeeId)} (ticket owner)");
        }

        if (names.Count > 0)
        {
            var responding = last is { } l && closedAt - l <= RespondingWindow;
            return new HumanState(
                "Yes",
                $"{string.Join(", ", names)} {(responding ? "was responding" : "was connected or assigned")} at closure time.",
                last);
        }

        var anyEvidenceSource = s.Interactions.Count > 0 || s.Handlers.Count > 0 || s.Assignments.Count > 0;
        return anyEvidenceSource
            ? new HumanState("No", "No human agent connection, reply, assignment or ticket owner was recorded at closure time.", null)
            : new HumanState("Unknown", "No interaction, handler or ownership evidence is stored for this ticket.", null);
    }

    private sealed record AfterState(bool Any, DateTime? FirstSentAt, string Assessment, string Detail, string? AgentName, long? InteractionId);

    private static AfterState HumanAfter(TicketTimelineSources s, Context ctx, DateTime closedAt, DateTime? reopenedAt)
    {
        bool InWindow(DateTime t) => t > closedAt && (reopenedAt is null || t < reopenedAt);

        var allHuman = HumanMessages(s);

        // Delayed event: a human acted before the closure, but TigerCS only learned of it afterwards.
        var delayedMessages = allHuman
            .Where(m => m.Message.SentAtUtc <= closedAt && m.Received is { } r && r > closedAt)
            .ToList();
        var delayedHandlers = s.Handlers
            .Where(h => h.ConnectedAtUtc is { } c && c <= closedAt && h.RecordedAtUtc > closedAt)
            .ToList();

        var afterMessages = allHuman.Where(m => InWindow(m.Message.SentAtUtc)).OrderBy(m => m.Message.SentAtUtc).ToList();
        var afterHandlers = s.Handlers.Where(h => InWindow(h.ConnectedAtUtc ?? h.RecordedAtUtc)).ToList();

        var first = afterMessages.FirstOrDefault();
        var firstAt = (DateTime?)first?.Message.SentAtUtc;
        if (afterHandlers.Count > 0)
        {
            var handlerAt = afterHandlers.Min(h => h.ConnectedAtUtc ?? h.RecordedAtUtc);
            firstAt = firstAt is null || handlerAt < firstAt ? handlerAt : firstAt;
        }

        if (delayedMessages.Count > 0 || delayedHandlers.Count > 0)
        {
            var when = delayedMessages.Select(m => m.Message.SentAtUtc).Concat(delayedHandlers.Select(h => h.ConnectedAtUtc!.Value)).Min();
            return new AfterState(
                true, firstAt ?? when, ClosureAssessments.DelayedEvent,
                "A human had already replied or connected before the closure, but TigerCS received that event only afterwards (delayed / out-of-order delivery), so the closure job could not see them.",
                delayedMessages.FirstOrDefault()?.Message.SenderName, delayedMessages.FirstOrDefault()?.Message.TicketInteractionId ?? delayedHandlers.FirstOrDefault()?.TicketInteractionId);
        }

        if (afterMessages.Count == 0 && afterHandlers.Count == 0)
        {
            return new AfterState(false, null, ClosureAssessments.NoHumanActivityAfterClosure, "No human reply or connection was recorded after this closure.", null, null);
        }

        var interactionIds = afterMessages.Select(m => m.Message.TicketInteractionId).Concat(afterHandlers.Select(h => h.TicketInteractionId)).Distinct().ToList();
        var sameSession = new List<long>();
        var newSession = new List<long>();
        var endedBefore = new List<long>();
        foreach (var interactionId in interactionIds)
        {
            var interaction = s.Interactions.FirstOrDefault(i => i.TicketInteractionId == interactionId);
            if (interaction is null)
            {
                continue;
            }

            var startedAt = interaction.InteractionStartedAtUtc ?? interaction.CreatedAtUtc;
            if (startedAt > closedAt)
            {
                newSession.Add(interactionId);
            }
            else if (interaction.EndedAtUtc is { } ended && ended <= closedAt)
            {
                endedBefore.Add(interactionId);
            }
            else
            {
                sameSession.Add(interactionId);
            }
        }

        var agent = first?.Message.SenderName ?? (afterHandlers.Count > 0 ? ctx.Name(afterHandlers[0].HandledByUserId) : null);
        var firstInteraction = first?.Message.TicketInteractionId ?? afterHandlers.FirstOrDefault()?.TicketInteractionId;

        if (sameSession.Count > 0)
        {
            return new AfterState(
                true, firstAt, ClosureAssessments.PrematureClosure,
                "A human replied in the same conversation the system had already closed the ticket for. No human was recorded as connected or responding when it closed, " +
                "so the closure followed the AI inactivity rule — but the conversation was not over, and the ticket stayed closed while a person worked it.",
                agent, firstInteraction);
        }

        if (newSession.Count > 0 && endedBefore.Count == 0)
        {
            return new AfterState(
                true, firstAt, ClosureAssessments.SeparateInteractionSession,
                "The human activity belongs to a conversation that started after this closure — a separate interaction session, not the one that was closed.",
                agent, firstInteraction);
        }

        return new AfterState(
            true, firstAt, ClosureAssessments.Inconclusive,
            "Human activity was recorded after the closure, but the stored evidence does not show whether it belongs to the closed conversation or a new one.",
            agent, firstInteraction);
    }

    private sealed record HumanMessage(TicketInteractionMessage Message, DateTime? Received);

    private static List<HumanMessage> HumanMessages(TicketTimelineSources s)
    {
        var result = new List<HumanMessage>();
        foreach (var interaction in s.Interactions)
        {
            if (!s.MessagesByInteraction.TryGetValue(interaction.TicketInteractionId, out var messages))
            {
                continue;
            }

            foreach (var message in messages.Where(m => m.Sender == InteractionMessageSender.HumanAgent))
            {
                result.Add(new HumanMessage(message, ReceivedAt(s, interaction, message.Sequence)));
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ helpers

    private static Dictionary<string, string> ParseKv(string? value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=', StringComparison.Ordinal);
            if (index > 0)
            {
                result[part[..index].Trim()] = part[(index + 1)..].Trim();
            }
        }

        return result;
    }

    /// <summary>An audit value that names a different ticket (several tickets can share a Genesys conversation) is not this ticket's.</summary>
    private static bool BelongsToTicket(Dictionary<string, string> kv, long ticketId) =>
        !kv.TryGetValue("TicketId", out var raw)
        || !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
        || id == ticketId;

    private static bool TryInt(Dictionary<string, string> kv, string key, out int value)
    {
        value = 0;
        return kv.TryGetValue(key, out var raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string Pick(Dictionary<string, string> kv, string key, string fallback = "(none)") =>
        kv.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
