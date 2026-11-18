using TigerCS.Application.Modules.Ticketing.ActivityTimeline;
using TigerCS.Domain.Audit;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Ticketing.ActivityTimeline;

/// <summary>
/// The Activity timeline for the 18 Nov 2026 WhatsApp ticket: the previous owner of an AI-handled ticket is the AI Agent (not "Unassigned"), the
/// First Response milestone replaces the meaningless "Running → Running" SLA row and sits at the real first reply, a late event after closure reads as
/// evidence (never a reopen), and a start time stored in the wrong zone is flagged instead of silently reordering the story.
/// </summary>
public class TimelineGenesysTimingAndOwnershipTests
{
    private const long TicketId = 900;
    private static readonly DateTime Start = new(2026, 11, 18, 15, 18, 24, DateTimeKind.Utc);
    private static readonly DateTime FirstHuman = new(2026, 11, 18, 15, 21, 8, DateTimeKind.Utc);
    private static readonly DateTime Processed = new(2026, 11, 18, 15, 34, 1, DateTimeKind.Utc);
    private static readonly Guid Moaz = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid AiAgent = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    private static TicketInteraction WhatsApp(DateTime startedAt) =>
        TicketInteraction.CreateFromGenesys(
            TicketId, WellKnownChannels.WhatsApp, "+971500000001", "conv-wa", null, null, null, null, null, startedAt, "Inbound", Start,
            isOriginatingInteraction: true, customerName: "Layla");

    private static TicketTimelineSources Sources(
        DateTime interactionStart, IReadOnlyList<TicketWorkflowEvent>? workflow = null, IReadOnlyList<TicketStatusHistory>? history = null,
        IReadOnlyList<TicketAssignmentHistoryEntry>? aiHistory = null, IReadOnlyList<AuditEntry>? audit = null)
    {
        var interaction = WhatsApp(interactionStart);
        typeof(TicketInteraction).GetProperty(nameof(TicketInteraction.TicketInteractionId))!.SetValue(interaction, 1L);
        return new TicketTimelineSources
        {
            TicketId = TicketId,
            TicketNumber = "TG-REG-20261118-0001",
            CreatedAtUtc = Start,
            Interactions = [interaction],
            MessagesByInteraction = new Dictionary<long, IReadOnlyList<TicketInteractionMessage>>
            {
                [1] =
                [
                    new TicketInteractionMessage(1, 1, InteractionMessageSender.VirtualAgent, "Bot", null, Start.AddSeconds(5), "Welcome"),
                    new TicketInteractionMessage(1, 2, InteractionMessageSender.HumanAgent, "Moaz", "g-moaz", FirstHuman, "Hello")
                ]
            },
            WorkflowEvents = workflow ?? [],
            StatusHistory = history ?? [],
            AssignmentHistory = aiHistory ?? [],
            AuditEntries = audit ?? [],
            EmployeeNames = new Dictionary<Guid, string> { [Moaz] = "Moaz Ibrahim Abou Chaar", [AiAgent] = "TigerCS AI Agent" },
            DepartmentNames = new Dictionary<int, string> { [10] = "Registration", [20] = "Call Center" }
        };
    }

    [Fact]
    public void TheDepartmentTransfer_NamesTheAiAgentAsPreviousOwner_NotUnassigned()
    {
        var transfer = TicketWorkflowEvent.ForTransfer(
            TicketId, FirstHuman, null, TicketTransferReason.SystemRouting, 10, 20, Moaz, "WhatsApp conversation accepted by a Call Center agent — Call Center first", Guid.NewGuid());
        var ai = TicketAssignmentHistoryEntry.ForGenesys(
            TicketId, 10, null, AiAgent, TicketAssignmentSource.GenesysAiAgent, null, Start, Start, "conv-wa", "AI Agent handled the conversation");

        var timeline = TicketActivityTimelineBuilder.Build(Sources(Start, workflow: [transfer], aiHistory: [ai]));

        var evt = Assert.Single(timeline.Events, e => e.EventType == "TicketTransferred");
        Assert.Equal("Registration / AI Agent", evt.PreviousValue);
        Assert.Equal("Call Center / Moaz Ibrahim Abou Chaar", evt.NewValue);
        Assert.Equal(FirstHuman, evt.OccurredAtUtc);
    }

    [Fact]
    public void RunningToRunning_BecomesTheFirstResponseMilestone_AtTheRealFirstReply()
    {
        var row = new TicketStatusHistory(
            TicketId, TicketStatusDimension.SlaState, (byte)SlaState.Running, (byte)SlaState.Running, null, true,
            $"First human response recorded (HumanAgentMessage) at {FirstHuman:O}; entered at {Processed:O}.", Guid.NewGuid(), Processed);
        var seed = new TicketStatusHistory(TicketId, TicketStatusDimension.SlaState, null, (byte)SlaState.Running, null, true, null, Guid.NewGuid(), Start);

        var timeline = TicketActivityTimelineBuilder.Build(Sources(Start, history: [seed, row]));

        Assert.DoesNotContain(timeline.Events, e => e.EventType == "SlaStateChanged");
        var milestone = Assert.Single(timeline.Events, e => e.EventType == "FirstResponseRecorded");
        Assert.Equal(FirstHuman, milestone.OccurredAtUtc);          // when the person actually replied ...
        Assert.Equal(Processed, milestone.RecordedAtUtc);           // ... and, separately, when TigerCS processed it
        Assert.Null(milestone.PreviousValue);
        Assert.Null(milestone.NewValue);
    }

    [Fact]
    public void ARealSlaStateChange_IsStillShown()
    {
        var breach = new TicketStatusHistory(TicketId, TicketStatusDimension.SlaState, (byte)SlaState.Running, (byte)SlaState.Breached, null, true, null, Guid.NewGuid(), Processed);

        var timeline = TicketActivityTimelineBuilder.Build(Sources(Start, history: [breach]));

        var evt = Assert.Single(timeline.Events, e => e.EventType == "SlaStateChanged");
        Assert.Equal("Running", evt.PreviousValue);
        Assert.Equal("Breached", evt.NewValue);
    }

    [Fact]
    public void ALateEventAfterClosure_ReadsAsRecordedEvidence_AndNeverAsAReopen()
    {
        var audit = new AuditEntry(
            Guid.NewGuid(), "GenesysEventIgnoredOnClosedTicket", "Ticket", TicketId.ToString(), null,
            $"TicketId={TicketId};ConversationId=conv-wa;TicketStatus=Closed;HumanSignal=HumanTranscriptLine;TicketChanged=false", Guid.NewGuid(), Processed);

        var timeline = TicketActivityTimelineBuilder.Build(Sources(Start, audit: [audit]));

        var evt = Assert.Single(timeline.Events, e => e.EventType == "LateEventIgnoredAfterClosure");
        Assert.Contains("not reopened", evt.Description);
        Assert.DoesNotContain(timeline.Events, e => e.EventType == "TicketReopened");
    }

    [Fact]
    public void AStartTimeStoredFourHoursLate_IsFlagged_NotSilentlyTrusted()
    {
        // The production symptom: "AI conversation started 19:18" although the first human reply was at 15:21.
        var timeline = TicketActivityTimelineBuilder.Build(Sources(Start.AddHours(4)));

        var started = Assert.Single(timeline.Events, e => e.EventType is "AiConversationStarted" or "ConversationStarted");
        Assert.Contains("wrong time zone", started.TimeNote);
        Assert.Contains(timeline.EvidenceGaps, g => g.Contains("wrong time zone"));
    }

    [Fact]
    public void AStartTimeStoredCorrectly_HasNoZoneWarning_AndTheTimelineIsChronological()
    {
        var timeline = TicketActivityTimelineBuilder.Build(Sources(Start));

        var started = Assert.Single(timeline.Events, e => e.EventType is "AiConversationStarted" or "ConversationStarted");
        Assert.Null(started.TimeNote);
        Assert.Equal(timeline.Events.OrderBy(e => e.OccurredAtUtc).Select(e => e.EventType), timeline.Events.Select(e => e.EventType));
    }
}
