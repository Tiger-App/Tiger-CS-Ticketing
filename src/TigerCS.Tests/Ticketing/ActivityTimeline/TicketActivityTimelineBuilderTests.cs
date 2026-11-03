// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using TigerCS.Application.Modules.Ticketing.ActivityTimeline;
using TigerCS.Domain.Audit;
using TigerCS.Domain.Modules.Ticketing;
using TigerCsWeb::TigerCS.Web.Models;

namespace TigerCS.Tests.Ticketing.ActivityTimeline;

/// <summary>
/// Regression tests for the ticket Activity timeline. Pure data in, timeline out: each scenario states what the stores recorded and asserts
/// what the timeline says — including what it refuses to say when evidence is missing. The base scenario mirrors production ticket 187
/// (Instagram, created 08:15 Dubai, inactivity closure 08:26, human reply 09:05); Dubai is UTC+4, so 08:15 Dubai is 04:15 UTC.
/// </summary>
public class TicketActivityTimelineBuilderTests
{
    private const long TicketId = 187;
    private const string Conversation = "conv-187";
    private static readonly DateTime T0 = new(2026, 11, 3, 4, 15, 0, DateTimeKind.Utc); // 08:15 Dubai
    private static readonly Guid Kawther = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Sara = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Integration = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid Boss = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly Dictionary<Guid, string> Names = new()
    {
        [Kawther] = "Kawther Benbrahim", [Sara] = "Sara Ali", [Integration] = "svc-genesys", [Boss] = "Mona Supervisor"
    };

    private static readonly Dictionary<int, string> Departments = new() { [10] = "Facilities Management", [20] = "Call Center" };

    // ---- builders ----

    private static T WithId<T>(T entity, string property, long id) where T : class
    {
        var prop = typeof(T).GetProperty(property)!;
        prop.SetValue(entity, Convert.ChangeType(id, prop.PropertyType.IsGenericType ? Nullable.GetUnderlyingType(prop.PropertyType)! : prop.PropertyType));
        return entity;
    }

    private static TicketInteraction Chat(long id, DateTime startedAt, string conversation = Conversation, bool originating = true, byte channel = WellKnownChannels.Instagram) =>
        WithId(
            TicketInteraction.CreateFromGenesys(
                TicketId, channel, "+971500000001", conversation, null, "q1", "Facilities queue", null, null,
                startedAt, "Inbound", startedAt, isOriginatingInteraction: originating, customerName: "Layla"),
            nameof(TicketInteraction.TicketInteractionId), id);

    private static TicketInteractionMessage Msg(long interactionId, int seq, InteractionMessageSender sender, DateTime at, string? name = null) =>
        new(interactionId, seq, sender, name, null, at, $"message {seq}");

    private static TicketStatusHistory Status(TicketStatus from, TicketStatus to, DateTime at, Guid? actor, Guid correlation, string? note = null, bool system = false) =>
        new(TicketId, TicketStatusDimension.TicketStatus, (byte)from, (byte)to, actor, system || actor is null, note, correlation, at);

    private static AuditEntry Audit(string action, DateTime at, Guid correlation, string? after = null, string entityType = "Ticket", string? entityId = null, string? before = null) =>
        new(Integration, action, entityType, entityId ?? TicketId.ToString(), before, after, correlation, at);

    private static TicketTimelineSources Sources(
        IReadOnlyList<TicketInteraction>? interactions = null,
        IReadOnlyDictionary<long, IReadOnlyList<TicketInteractionMessage>>? messages = null,
        IReadOnlyList<TicketAgentHandoff>? handoffs = null,
        IReadOnlyList<TicketInteractionHandler>? handlers = null,
        IReadOnlyList<TicketWorkflowEvent>? workflow = null,
        IReadOnlyList<TicketStatusHistory>? history = null,
        IReadOnlyList<TicketAssignment>? assignments = null,
        IReadOnlyList<AuditEntry>? audit = null,
        Guid? createdBy = null,
        IReadOnlySet<Guid>? supervisors = null) => new()
    {
        TicketId = TicketId,
        TicketNumber = "TG-FM-20261103-0001",
        CreatedAtUtc = T0,
        CreatedByEmployeeId = createdBy ?? Integration,
        Interactions = interactions ?? [],
        MessagesByInteraction = messages ?? new Dictionary<long, IReadOnlyList<TicketInteractionMessage>>(),
        ChannelNames = new Dictionary<byte, string> { [WellKnownChannels.Instagram] = "Instagram", [WellKnownChannels.Phone] = "Phone" },
        Handoffs = handoffs ?? [],
        Handlers = handlers ?? [],
        WorkflowEvents = workflow ?? [],
        StatusHistory = history ?? [],
        Assignments = assignments ?? [],
        AuditEntries = audit ?? [],
        EmployeeNames = Names,
        DepartmentNames = Departments,
        SupervisorIds = supervisors ?? new HashSet<Guid>()
    };

    private static IReadOnlyDictionary<long, IReadOnlyList<TicketInteractionMessage>> Messages(long id, params TicketInteractionMessage[] messages) =>
        new Dictionary<long, IReadOnlyList<TicketInteractionMessage>> { [id] = messages };

    private static TicketActivityEventDto Single(TicketActivityTimelineDto t, string eventType) =>
        Assert.Single(t.Events, e => e.EventType == eventType);

    /// <summary>The production inactivity closure of ticket 187: Open → Resolved → Closed at 08:26 Dubai, by the system.</summary>
    private static (IReadOnlyList<TicketStatusHistory> History, IReadOnlyList<AuditEntry> Audit) InactivityClosure187()
    {
        var correlation = Guid.NewGuid();
        const string reason = "Automatically closed — customer inactive for more than 10 minutes.";
        var closedAt = T0.AddMinutes(11);
        return (
            [
                Status(TicketStatus.Open, TicketStatus.Resolved, closedAt, null, correlation, reason, system: true),
                Status(TicketStatus.Resolved, TicketStatus.Closed, closedAt, null, correlation, reason, system: true)
            ],
            [Audit(TimelineAuditActions.AutoCloseCustomerInactivity, closedAt, correlation, $"Closed;ResolutionOutcome=Cancelled;Reason={reason}")]);
    }

    // ---- 1. AI-only resolution and closure ----

    [Fact]
    public void AiOnlyResolution_ShowsAiMilestones_AndClosedByAi_WithNoHumanInvolved()
    {
        var interaction = Chat(1, T0);
        var correlation = Guid.NewGuid();
        var closedAt = T0.AddMinutes(4);
        var history = new[]
        {
            Status(TicketStatus.Open, TicketStatus.Resolved, closedAt, null, correlation, "Closed by AI", system: true),
            Status(TicketStatus.Resolved, TicketStatus.Closed, closedAt, null, correlation, "Closed by AI", system: true)
        };
        var audit = new[] { Audit(TimelineAuditActions.AutoCloseByAi, closedAt, correlation, "Closed;ResolutionOutcome=Resolved;Reason=Closed by AI;ConversationId=conv-187") };

        var t = TicketActivityTimelineBuilder.Build(Sources(
            [interaction],
            Messages(1,
                Msg(1, 1, InteractionMessageSender.Customer, T0.AddSeconds(5)),
                Msg(1, 2, InteractionMessageSender.VirtualAgent, T0.AddSeconds(10), "Tiger Bot"),
                Msg(1, 3, InteractionMessageSender.Customer, T0.AddMinutes(1)),
                Msg(1, 4, InteractionMessageSender.VirtualAgent, T0.AddMinutes(3), "Tiger Bot")),
            history: history, audit: audit));

        Assert.Equal("AI conversation started", Single(t, "AiConversationStarted").EventName);
        Assert.Equal(T0.AddSeconds(10), Single(t, "AiFirstResponse").OccurredAtUtc);
        Assert.Equal(T0.AddMinutes(3), Single(t, "AiLastResponse").OccurredAtUtc);

        var closed = Single(t, "TicketClosed");
        var c = Assert.IsType<TicketClosureDetailsDto>(closed.Closure);
        Assert.Equal(ClosedByTypes.Ai, c.ClosedByType);
        Assert.Equal(TimelineActorTypes.Ai, closed.ActorType);
        Assert.Equal("Closed by AI", c.Reason);
        Assert.Equal("AiResolved", c.ReasonCode);
        Assert.Equal("No", c.HumanAgentConnected);
        Assert.Equal(ClosureAssessments.NoHumanActivityAfterClosure, c.Assessment);
        Assert.DoesNotContain(t.Events, e => e.EventType is "HumanAgentConnected" or "FirstHumanResponse");
    }

    // ---- 2. AI -> human handoff ----

    [Fact]
    public void AiToHumanHandoff_ShowsRequestQueuedOffered_AndNamesWhoAskedAndWhy()
    {
        var interaction = Chat(1, T0);
        var handoff = new TicketAgentHandoff(
            TicketId, 1, 10, WellKnownChannels.Instagram, T0.AddMinutes(2), T0.AddMinutes(2), AgentHandoffMode.ContinueChat,
            HandoffTrigger.CustomerRequestedHuman, "Customer typed 'agent please'");
        handoff.AssignTo(Kawther, "gen-kawther", T0.AddMinutes(3));

        var t = TicketActivityTimelineBuilder.Build(Sources([interaction], handoffs: [handoff]));

        var asked = Single(t, "HumanAssistanceRequested");
        Assert.Equal("Customer requested human assistance", asked.EventName);
        Assert.Equal(TimelineActorTypes.Customer, asked.ActorType);
        Assert.Equal("Customer typed 'agent please'", asked.Reason);
        Assert.Equal(AgentHandoffStatus.WaitingForAgent.ToString(), Single(t, "GenesysHandoffQueued").NewValue);

        var offered = Single(t, "GenesysHandoffOffered");
        Assert.Equal(T0.AddMinutes(3), offered.OccurredAtUtc);
        Assert.Contains("Kawther Benbrahim", offered.Description);
        Assert.Equal(AgentHandoffStatus.Assigned.ToString(), offered.NewValue);
    }

    [Fact]
    public void HandoffWithoutAStatedTrigger_IsNotAttributedToAnyone()
    {
        var handoff = new TicketAgentHandoff(TicketId, 1, 10, WellKnownChannels.Instagram, T0.AddMinutes(2), T0.AddMinutes(2));

        var t = TicketActivityTimelineBuilder.Build(Sources([Chat(1, T0)], handoffs: [handoff]));

        var asked = Single(t, "HumanAssistanceRequested");
        Assert.Equal("Human assistance requested", asked.EventName);
        Assert.Equal(TimelineActorTypes.Unknown, asked.ActorType);
        Assert.Equal(TimelineWording.Unknown, asked.ActorName);
        Assert.Equal(TimelineWording.NotRecorded, asked.Reason);
    }

    // ---- 3. Agent accepted and replied ----

    [Fact]
    public void AgentAcceptedAndReplied_ShowsConnected_Accepted_FirstAndLatestHumanResponse_WithoutFloodingMessages()
    {
        var interaction = Chat(1, T0);
        var handoff = new TicketAgentHandoff(
            TicketId, 1, 10, WellKnownChannels.Instagram, T0.AddMinutes(2), T0.AddMinutes(2), trigger: HandoffTrigger.CustomerRequestedHuman);
        handoff.Start(Kawther, T0.AddMinutes(5));
        var handler = TicketInteractionHandler.Create(1, TicketId, Kawther, "gen-kawther", InteractionHandlerSource.Routing, T0.AddMinutes(4), T0.AddMinutes(4));

        var human = Enumerable.Range(1, 12)
            .Select(i => Msg(1, i, InteractionMessageSender.HumanAgent, T0.AddMinutes(5 + i), "Kawther Benbrahim"))
            .ToArray();

        var t = TicketActivityTimelineBuilder.Build(Sources([interaction], Messages(1, human), [handoff], [handler]));

        var connected = Single(t, "HumanAgentConnected");
        Assert.Equal("Kawther Benbrahim", connected.ActorName);
        Assert.Contains("gen-kawther", connected.Description); // the Genesys ID → TigerCS user mapping is shown
        Assert.Equal(T0.AddMinutes(4), connected.OccurredAtUtc);

        Assert.Equal("Handoff accepted by agent", Single(t, "GenesysHandoffAccepted").EventName);
        Assert.Equal(T0.AddMinutes(6), Single(t, "FirstHumanResponse").OccurredAtUtc);
        Assert.Equal(T0.AddMinutes(17), Single(t, "LatestHumanResponse").OccurredAtUtc);
        Assert.Contains("12 human message", Single(t, "FirstHumanResponse").Description);

        // 12 human messages → two milestones, never twelve rows; each links to the conversation.
        Assert.Equal(2, t.Events.Count(e => e.Source == "TicketInteractionMessages" && e.EventType.Contains("Human")));
        Assert.All(t.Events.Where(e => e.EventType.EndsWith("HumanResponse")), e => Assert.Equal(1, e.TicketInteractionId));
    }

    // ---- 4. Automatic inactivity closure (production ticket 187, as recorded) ----

    [Fact]
    public void InactivityClosure_ShowsSystemAutomation_Reason_EndedNo_HumanNo()
    {
        var (history, audit) = InactivityClosure187();
        var interaction = Chat(1, T0);

        var t = TicketActivityTimelineBuilder.Build(Sources(
            [interaction],
            Messages(1, Msg(1, 1, InteractionMessageSender.Customer, T0.AddSeconds(3)), Msg(1, 2, InteractionMessageSender.VirtualAgent, T0.AddSeconds(8))),
            history: history, audit: audit));

        var closed = Single(t, "TicketClosed");
        var c = Assert.IsType<TicketClosureDetailsDto>(closed.Closure);
        Assert.Equal(T0.AddMinutes(11), c.ClosedAtUtc);
        Assert.Equal(ClosedByTypes.SystemAutomation, c.ClosedByType);
        Assert.Equal("CustomerInactivity", c.ReasonCode);
        Assert.Equal("Audit entry", c.EvidenceSource);
        Assert.Contains("customer inactive for more than 10 minutes", c.Reason);
        Assert.Equal("No", c.CustomerInteractionEnded);   // the chat was still open
        Assert.Equal("No", c.HumanAgentConnected);        // nobody recorded
        Assert.Equal(ClosureAssessments.NoHumanActivityAfterClosure, c.Assessment);
        Assert.Equal("Resolved", closed.PreviousValue); // Open → Resolved → Closed: the Closed row follows Resolved
        Assert.Equal("Closed", closed.NewValue);

        // The Open → Resolved step of the same closure is its own lifecycle row.
        Assert.Equal("Ticket resolved", Single(t, "StatusResolved").EventName);
    }

    // ---- 5. Delayed / out-of-order Genesys events ----

    [Fact]
    public void DelayedGenesysConnectEvent_IsPlacedAtItsOwnTime_AndFlaggedAsDelayed_AndClosureIsAssessedAsDelayedEvent()
    {
        var (history, audit) = InactivityClosure187();
        var interaction = Chat(1, T0);
        // The agent connected at 04:20 UTC (before the 04:26 closure), but TigerCS only recorded it at 05:05 UTC.
        var handler = TicketInteractionHandler.Create(1, TicketId, Kawther, "gen-kawther", InteractionHandlerSource.Routing, T0.AddMinutes(5), T0.AddMinutes(50));

        var t = TicketActivityTimelineBuilder.Build(Sources([interaction], handlers: [handler], history: history, audit: audit));

        var connected = Single(t, "HumanAgentConnected");
        Assert.Equal(T0.AddMinutes(5), connected.OccurredAtUtc);          // ordered by when it happened…
        Assert.Equal(T0.AddMinutes(50), connected.RecordedAtUtc);          // …with the late recording shown
        Assert.Contains("delayed", connected.TimeNote);
        Assert.True(t.Events.ToList().IndexOf(connected) < t.Events.ToList().IndexOf(Single(t, "TicketClosed")));

        var c = Single(t, "TicketClosed").Closure!;
        Assert.Equal(ClosureAssessments.DelayedEvent, c.Assessment);
        Assert.True(c.HumanActivityAfterClosure);
        Assert.Equal("Yes", c.HumanAgentConnected);
    }

    [Fact]
    public void DelayedTranscriptDelivery_IsRecognisedFromTheAuditReceiptTime()
    {
        var (history, audit) = InactivityClosure187();
        var interaction = Chat(1, T0);
        // Sent at 04:24 (before the 04:26 closure) but stored by TigerCS at 05:05 — the audit entry covers sequence 1.
        var human = Msg(1, 1, InteractionMessageSender.HumanAgent, T0.AddMinutes(9), "Kawther Benbrahim");
        var receipt = Audit(
            "GenesysTranscriptAppended", T0.AddMinutes(50), Guid.NewGuid(), $"TicketId={TicketId};Messages=1;Appended=1;FirstHumanResponseRecordedAtUtc=(none)",
            entityType: "GenesysConversation", entityId: Conversation);

        var t = TicketActivityTimelineBuilder.Build(Sources([interaction], Messages(1, human), history: history, audit: [.. audit, receipt]));

        var first = Single(t, "FirstHumanResponse");
        Assert.Equal(T0.AddMinutes(9), first.OccurredAtUtc);
        Assert.Equal(T0.AddMinutes(50), first.RecordedAtUtc);
        Assert.Equal(ClosureAssessments.DelayedEvent, Single(t, "TicketClosed").Closure!.Assessment);
    }

    // ---- 6. Human replies after an earlier closure ----

    [Fact]
    public void HumanReplyInTheSameConversationAfterClosure_IsAPrematureClosure_AndIsFlagged()
    {
        var (history, audit) = InactivityClosure187();
        var interaction = Chat(1, T0);
        // 09:05 Dubai = 05:05 UTC — the production observation.
        var reply = Msg(1, 3, InteractionMessageSender.HumanAgent, T0.AddMinutes(50), "Kawther Benbrahim");

        var t = TicketActivityTimelineBuilder.Build(Sources(
            [interaction],
            Messages(1, Msg(1, 1, InteractionMessageSender.Customer, T0.AddSeconds(3)), Msg(1, 2, InteractionMessageSender.VirtualAgent, T0.AddSeconds(8)), reply),
            history: history, audit: audit));

        var closure = Single(t, "TicketClosed").Closure!;
        Assert.Equal("Warning", Single(t, "TicketClosed").Severity);
        Assert.True(closure.HumanActivityAfterClosure);
        Assert.Equal(T0.AddMinutes(50), closure.FirstHumanActivityAfterClosureUtc);
        Assert.Equal(ClosureAssessments.PrematureClosure, closure.Assessment);
        Assert.Equal("No", closure.HumanAgentConnected); // no human was recorded when it closed — the closure followed the AI rule

        var anomaly = Single(t, "HumanActivityAfterClosure");
        Assert.Equal("Warning", anomaly.Severity);
        Assert.Equal("Kawther Benbrahim", anomaly.ActorName);
        Assert.Equal(T0.AddMinutes(50), anomaly.OccurredAtUtc);
    }

    [Fact]
    public void HumanReplyInANewConversationStartedAfterClosure_IsASeparateInteractionSession()
    {
        var (history, audit) = InactivityClosure187();
        var first = Chat(1, T0);
        var second = Chat(2, T0.AddMinutes(45), conversation: "conv-187-b", originating: false);

        var t = TicketActivityTimelineBuilder.Build(Sources(
            [first, second],
            Messages(2, Msg(2, 1, InteractionMessageSender.Customer, T0.AddMinutes(45)), Msg(2, 2, InteractionMessageSender.HumanAgent, T0.AddMinutes(50), "Kawther Benbrahim")),
            history: history, audit: audit));

        var closure = Single(t, "TicketClosed").Closure!;
        Assert.Equal(ClosureAssessments.SeparateInteractionSession, closure.Assessment);
        Assert.True(closure.HumanActivityAfterClosure);
    }

    [Fact]
    public void ReplyAfterAReopen_IsNotCountedAgainstTheEarlierClosure()
    {
        var (history, audit) = InactivityClosure187();
        var reopen = Status(TicketStatus.Closed, TicketStatus.InProgress, T0.AddMinutes(30), Boss, Guid.NewGuid(), "Customer came back");
        var reply = Msg(1, 1, InteractionMessageSender.HumanAgent, T0.AddMinutes(50), "Kawther Benbrahim");

        var t = TicketActivityTimelineBuilder.Build(Sources([Chat(1, T0)], Messages(1, reply), history: [.. history, reopen], audit: audit));

        Assert.Equal(ClosureAssessments.NoHumanActivityAfterClosure, Single(t, "TicketClosed").Closure!.Assessment);
        var reopened = Single(t, "TicketReopened");
        Assert.Equal("Mona Supervisor", reopened.ActorName);
        Assert.Equal("Closed", reopened.PreviousValue);
        Assert.Equal("Customer came back", reopened.Reason);
    }

    // ---- 7. Multiple interactions on one ticket ----

    [Fact]
    public void MultipleInteractions_AreLabelledAndKeptSeparate_AndEachLinksToItsConversation()
    {
        var first = Chat(1, T0);
        var phone = WithId(
            TicketInteraction.CreateFromGenesys(TicketId, WellKnownChannels.Phone, "+971500000001", "conv-call", null, null, "Call Center", null, null, T0.AddHours(2), "Inbound", T0.AddHours(2), customerName: "Layla"),
            nameof(TicketInteraction.TicketInteractionId), 2);
        phone.End(T0.AddHours(2).AddMinutes(8), "AgentDisconnect");

        var t = TicketActivityTimelineBuilder.Build(Sources([first, phone]));

        var starts = t.Events.Where(e => e.EventType is "ConversationStarted" or "AiConversationStarted").ToList();
        Assert.Equal(2, starts.Count);
        Assert.Contains("interaction 1 of 2 (Instagram)", starts[0].Description);
        Assert.Contains("interaction 2 of 2 (Phone)", starts[1].Description);
        Assert.Equal([1L, 2L], starts.Select(e => e.TicketInteractionId!.Value).ToArray());

        var ended = Single(t, "ConversationEnded");
        Assert.Equal(2, ended.TicketInteractionId);
        Assert.Equal("AgentDisconnect", ended.Reason);
        Assert.Equal(TimelineActorTypes.Agent, ended.ActorType);
        Assert.Contains("derived", ended.Description);
    }

    // ---- 8. Missing audit evidence ----

    [Fact]
    public void MissingAuditEvidence_IsShownAsUnknownOrNotRecorded_NeverInvented()
    {
        var correlation = Guid.NewGuid();
        var closedAt = T0.AddMinutes(11);
        // System closure, no audit entry, no note: nothing says why.
        var history = new[]
        {
            Status(TicketStatus.Open, TicketStatus.Resolved, closedAt, null, correlation, system: true),
            Status(TicketStatus.Resolved, TicketStatus.Closed, closedAt, null, correlation, system: true)
        };
        var legacy = TicketInteraction.CreateFromGenesys(TicketId, WellKnownChannels.Instagram, "+971500000001", Conversation, null, null, null, "gen-x", "Unmapped Agent", null, null, T0);

        var t = TicketActivityTimelineBuilder.Build(Sources([WithId(legacy, nameof(TicketInteraction.TicketInteractionId), 1)], history: history));

        var c = Single(t, "TicketClosed").Closure!;
        Assert.Equal(ClosedByTypes.SystemAutomation, c.ClosedByType);
        Assert.Equal(TimelineWording.NotRecorded, c.ClosedByName);
        Assert.Equal(TimelineWording.NotRecorded, c.Reason);
        Assert.Equal("NotRecorded", c.ReasonCode);
        Assert.Contains(t.EvidenceGaps, g => g.Contains("no audit entry", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(t.EvidenceGaps, g => g.Contains("not mapped to a TigerCS user"));

        // No start time from Genesys: the timeline says it is showing the recording time instead of claiming one.
        Assert.Contains("did not supply a start time", Single(t, "ConversationStarted").TimeNote);
    }

    [Fact]
    public void MissingCreatorAndInteractions_AreReportedAsNotRecorded()
    {
        var t = TicketActivityTimelineBuilder.Build(new TicketTimelineSources
        {
            TicketId = TicketId, TicketNumber = "TG-OLD-1", CreatedAtUtc = T0, CreatedByEmployeeId = null
        });

        var created = Single(t, "TicketCreated");
        Assert.Equal(TimelineWording.NotRecorded, created.ActorName);
        Assert.Equal(TimelineActorTypes.Unknown, created.ActorType);
        Assert.Contains("Source: Not recorded", created.Description);
        Assert.Contains("Channel: Not recorded", created.Description);
        Assert.Equal(2, t.EvidenceGaps.Count);
    }

    [Fact]
    public void ClosureWithoutAnAuditEntry_ButWithARecognisedNote_StatesThatTheNoteWasTheEvidence()
    {
        var correlation = Guid.NewGuid();
        var at = T0.AddMinutes(11);
        var history = new[]
        {
            Status(TicketStatus.Resolved, TicketStatus.Closed, at, null, correlation, "Automatically closed — customer inactive for more than 10 minutes.", system: true)
        };

        var c = Single(TicketActivityTimelineBuilder.Build(Sources([Chat(1, T0)], history: history)), "TicketClosed").Closure!;

        Assert.Equal("CustomerInactivity", c.ReasonCode);
        Assert.Equal("Lifecycle history note", c.EvidenceSource);
    }

    // ---- ownership, transfers, human closure ----

    [Fact]
    public void OwnershipAndDepartmentTransfer_ShowPreviousAndReceivingOwner_WithoutDuplicatingTheAssignment()
    {
        var assigned = new TicketAssignment(TicketId, Sara, 10, T0.AddMinutes(1), null);
        var moved = T0.AddMinutes(30);
        var transfer = TicketWorkflowEvent.ForTransfer(TicketId, moved, Sara, TicketTransferReason.WrongDepartment, 10, 20, Kawther, "Not a facilities matter", Guid.NewGuid());
        var assignedByTransfer = new TicketAssignment(TicketId, Kawther, 20, moved, Sara);

        var t = TicketActivityTimelineBuilder.Build(Sources([Chat(1, T0)], workflow: [transfer], assignments: [assigned, assignedByTransfer]));

        var first = Single(t, "OwnerAssigned");
        Assert.Equal("Unassigned (department queue)", first.PreviousValue);
        Assert.Equal("Sara Ali", first.NewValue);
        Assert.Contains("automatic", first.ActorName);

        var transferred = Single(t, "TicketTransferred");
        Assert.Equal("Department transfer", transferred.EventName);
        Assert.Equal("Facilities Management / Sara Ali", transferred.PreviousValue);
        Assert.Equal("Call Center / Kawther Benbrahim", transferred.NewValue);
        Assert.Equal("Sara Ali", transferred.ActorName);
        Assert.DoesNotContain(t.Events, e => e.EventType == "OwnerChanged"); // the assignment the transfer caused is not told twice
    }

    [Fact]
    public void ReleasedOwnership_IsShownAsReturnedToTheQueue()
    {
        var assigned = new TicketAssignment(TicketId, Sara, 10, T0.AddMinutes(1), null);
        var release = TicketWorkflowEvent.ForTransfer(TicketId, T0.AddMinutes(20), Sara, TicketTransferReason.WrongDepartment, 10, 10, null, null, Guid.NewGuid());

        var transferred = Single(TicketActivityTimelineBuilder.Build(Sources([Chat(1, T0)], workflow: [release], assignments: [assigned])), "TicketTransferred");

        Assert.Equal("Ownership released", transferred.EventName);
        Assert.Equal("Sara Ali", transferred.PreviousValue);
        Assert.Equal("department queue (unassigned)", transferred.NewValue);
    }

    [Fact]
    public void HumanClosure_ShowsTheActualActor_AndSupervisorWhenTheirRoleSaysSo()
    {
        var correlation = Guid.NewGuid();
        var resolve = Status(TicketStatus.InProgress, TicketStatus.Resolved, T0.AddMinutes(30), Sara, Guid.NewGuid(), "Fixed the AC");
        var agentClose = Status(TicketStatus.Resolved, TicketStatus.Closed, T0.AddMinutes(31), Sara, correlation, null);
        var handler = TicketInteractionHandler.Create(1, TicketId, Sara, "gen-sara", InteractionHandlerSource.Routing, T0.AddMinutes(2), T0.AddMinutes(2));

        var agent = Single(TicketActivityTimelineBuilder.Build(Sources([Chat(1, T0)], handlers: [handler], history: [resolve, agentClose])), "TicketClosed").Closure!;
        Assert.Equal(ClosedByTypes.HumanAgent, agent.ClosedByType);
        Assert.Equal("Sara Ali", agent.ClosedByName);
        Assert.Equal("ResolvedByAgent", agent.ReasonCode);
        Assert.Equal("Yes", agent.HumanAgentConnected);
        Assert.Contains("Sara Ali", agent.HumanAgentDetail);

        var supervisorClose = Status(TicketStatus.InProgress, TicketStatus.Closed, T0.AddMinutes(40), Boss, Guid.NewGuid(), "Duplicate of another ticket");
        var sup = Single(
            TicketActivityTimelineBuilder.Build(Sources([Chat(1, T0)], history: [supervisorClose], supervisors: new HashSet<Guid> { Boss })), "TicketClosed").Closure!;
        Assert.Equal(ClosedByTypes.Supervisor, sup.ClosedByType);
        Assert.Equal("Mona Supervisor", sup.ClosedByName);
        Assert.Equal("Duplicate of another ticket", sup.Reason);
        Assert.Equal("ManualClose", sup.ReasonCode);
    }

    [Fact]
    public void ClosureWhileAHumanIsStillRespondingByTheSystem_IsFlaggedAsAWarning()
    {
        var (history, audit) = InactivityClosure187();
        var human = Msg(1, 1, InteractionMessageSender.HumanAgent, T0.AddMinutes(10), "Kawther Benbrahim");

        var closed = Single(TicketActivityTimelineBuilder.Build(Sources([Chat(1, T0)], Messages(1, human), history: history, audit: audit)), "TicketClosed");

        Assert.Equal("Warning", closed.Severity);
        Assert.Equal("Yes", closed.Closure!.HumanAgentConnected);
        Assert.Contains("was responding", closed.Closure.HumanAgentDetail);
        Assert.Equal(T0.AddMinutes(10), closed.Closure.LastHumanActivityBeforeClosureUtc);
    }

    [Fact]
    public void SlaAndRoutingEvents_AppearOnTheTimeline_WithPreviousAndNewValues()
    {
        var sla = new TicketStatusHistory(TicketId, TicketStatusDimension.SlaState, (byte)SlaState.Running, (byte)SlaState.Breached, null, true, null, Guid.NewGuid(), T0.AddHours(1));
        var routing = Audit(
            "GenesysRoutingChanged", T0.AddMinutes(3), Guid.NewGuid(),
            after: $"TicketId={TicketId};QueueId=q2;QueueName=Call Center;AgentId=gen-1;AgentName=Kawther;StartedAtUtc=(none)",
            before: $"TicketId={TicketId};QueueId=q1;QueueName=Facilities queue;AgentId=(none);AgentName=(none);StartedAtUtc=(none)",
            entityType: "GenesysConversation", entityId: Conversation);
        var otherTicket = Audit(
            "GenesysRoutingChanged", T0.AddMinutes(4), Guid.NewGuid(), after: "TicketId=999;QueueName=Elsewhere", before: "TicketId=999;QueueName=X",
            entityType: "GenesysConversation", entityId: Conversation);

        var t = TicketActivityTimelineBuilder.Build(Sources([Chat(1, T0)], history: [sla], audit: [routing, otherTicket]));

        var slaEvent = Single(t, "SlaStateChanged");
        Assert.Equal("Running", slaEvent.PreviousValue);
        Assert.Equal("Breached", slaEvent.NewValue);

        var routed = Single(t, "ConversationRouted"); // the sibling ticket's routing entry is not this ticket's
        Assert.Equal("Queue: Facilities queue; Agent: (none)", routed.PreviousValue);
        Assert.Equal("Queue: Call Center; Agent: Kawther", routed.NewValue);
    }

    // ---- ordering ----

    [Fact]
    public void Timeline_IsChronological_WithCreationFirst()
    {
        var (history, audit) = InactivityClosure187();
        var human = Msg(1, 1, InteractionMessageSender.HumanAgent, T0.AddMinutes(50), "Kawther Benbrahim");

        var t = TicketActivityTimelineBuilder.Build(Sources([Chat(1, T0)], Messages(1, human), history: history, audit: audit));

        Assert.Equal("TicketCreated", t.Events[0].EventType);
        Assert.Equal(t.Events.OrderBy(e => e.OccurredAtUtc).Select(e => e.EventType), t.Events.Select(e => e.EventType));
    }

    // ---- 6. timestamps: Asia/Dubai ----

    [Fact]
    public void Timestamps_AreConvertedToAsiaDubai_AndLabelled()
    {
        // 04:26:41 UTC is 08:26:41 in Dubai (UTC+4, no daylight saving) — whatever the server's own zone is.
        var utc = new DateTime(2026, 11, 3, 4, 26, 41, DateTimeKind.Utc);
        Assert.Equal("3 Nov 2026 08:26:41", TicketDisplay.DubaiTimestamp(utc));
        Assert.Equal("3 Nov 2026 08:26:41", TicketDisplay.DubaiTimestamp(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified)));
        Assert.Equal("Asia/Dubai (UTC+4)", TicketDisplay.ActivityTimeZoneLabel);

        // Crossing midnight: 21:00 UTC is 01:00 the next day in Dubai.
        Assert.Equal("4 Nov 2026 01:00:00", TicketDisplay.DubaiTimestamp(new DateTime(2026, 11, 3, 21, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void NoDescriptionOrNoteCarriesAClockTime_SoNoTextCanDisagreeWithTheTimestampColumn()
    {
        var (history, audit) = InactivityClosure187();
        var handler = TicketInteractionHandler.Create(1, TicketId, Kawther, "gen-kawther", InteractionHandlerSource.Routing, T0.AddMinutes(5), T0.AddMinutes(50));
        var human = Msg(1, 1, InteractionMessageSender.HumanAgent, T0.AddMinutes(50), "Kawther Benbrahim");

        var t = TicketActivityTimelineBuilder.Build(Sources([Chat(1, T0)], Messages(1, human), handlers: [handler], history: history, audit: audit));

        var clock = new System.Text.RegularExpressions.Regex(@"\b\d{1,2}:\d{2}\b|\d{4}-\d{2}-\d{2}");
        foreach (var e in t.Events)
        {
            Assert.DoesNotMatch(clock, e.Description);
            Assert.DoesNotMatch(clock, e.TimeNote ?? string.Empty);
        }

        foreach (var c in t.Closures)
        {
            Assert.DoesNotMatch(clock, c.AssessmentDetail);
            Assert.DoesNotMatch(clock, c.HumanAgentDetail);
            Assert.DoesNotMatch(clock, c.CustomerInteractionEndedDetail);
        }
    }
}
