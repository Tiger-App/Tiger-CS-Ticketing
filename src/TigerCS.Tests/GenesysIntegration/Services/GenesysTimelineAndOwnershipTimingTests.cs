using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The WhatsApp timeline defects of 18 Nov 2026: a timestamp that carried an offset was stored as UAE wall-clock time and shifted again by the UI; the
/// person who accepted a conversation at 15:21 was recorded as taking it at 15:34 (when the end of the conversation delivered their first line); and
/// the transfer was worded as a voice call. Runs the real ingestion/update/assignment services over fakes with a controlled clock.
/// </summary>
public class GenesysTimelineAndOwnershipTimingTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private static readonly DateTime Start = new(2026, 11, 18, 15, 18, 24, DateTimeKind.Utc);
    private static readonly DateTime FirstHumanLine = new(2026, 11, 18, 15, 21, 8, DateTimeKind.Utc);
    private static readonly DateTime EndProcessed = new(2026, 11, 18, 15, 34, 1, DateTimeKind.Utc);

    // ---- one time zone, converted once ----

    [Fact]
    public void ToUtc_KeepsUtc_ConvertsLocal_AndReadsAZonelessValueAsUtc()
    {
        var utc = new DateTime(2026, 11, 18, 15, 18, 24, DateTimeKind.Utc);
        Assert.Equal(utc, GenesysTimestamps.ToUtc(utc));

        // "2026-11-18T19:18:24+04:00" as System.Text.Json hands it over on a server in any zone: Local kind, same instant.
        var local = DateTimeOffset.Parse("2026-11-18T19:18:24+04:00").LocalDateTime;
        Assert.Equal(utc, GenesysTimestamps.ToUtc(local));
        Assert.Equal(DateTimeKind.Utc, GenesysTimestamps.ToUtc(local).Kind);

        // No zone at all: the contract says UTC, so it is never guessed to be local.
        Assert.Equal(utc, GenesysTimestamps.ToUtc(new DateTime(2026, 11, 18, 15, 18, 24, DateTimeKind.Unspecified)));
    }

    [Fact]
    public async Task AnOffsetBearingStartTime_IsStoredAsTheSameInstantInUtc_NotAsUaeWallClock()
    {
        var f = new GenesysServiceFixture();
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var asLocal = DateTimeOffset.Parse("2026-11-18T19:18:24+04:00").LocalDateTime;

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto("conv-tz", GenesysChannel.WhatsApp, CustomerPhone: "+971500000001", DepartmentId: cs.DepartmentId, StartedAtUtc: asLocal));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var stored = f.Interactions.All.Single(i => i.TicketId == result.Ticket!.TicketId).InteractionStartedAtUtc;
        Assert.Equal(Start, stored);
        Assert.Equal(DateTimeKind.Utc, stored!.Value.Kind);
    }

    // ---- ownership happens when the person accepted, not when TigerCS heard of it ----

    private sealed record Whatsapp(GenesysServiceFixture F, Ticket Ticket, Guid AgentId, string AgentGenesysId);

    private static async Task<Whatsapp> AiConversationAsync(GenesysChannel channel = GenesysChannel.WhatsApp)
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true);
        f.Clock.Set(Start);
        var (registration, _) = f.SeedGenesysDepartment("Registration", "REG");
        var agentId = f.AddAgent("g-moaz", "Moaz Ibrahim Abou Chaar");

        var ingested = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto("conv-wa", channel, CustomerPhone: "+971500000001", DepartmentId: registration.DepartmentId, StartedAtUtc: Start));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, ingested.Outcome);
        return new Whatsapp(f, f.Tickets.All.Single(t => t.TicketId == ingested.Ticket!.TicketId), agentId, "g-moaz");
    }

    private static GenesysTicketUpdateDto EndWithHumanLine(string agentGenesysId) => new(
        "conv-wa",
        Ended: new GenesysConversationEndUpdateDto(
            EndProcessed, "Disconnect",
            [
                new GenesysTranscriptMessageDto("VirtualAgent", Start.AddSeconds(5), "Welcome", "Bot", ExternalMessageId: "m1"),
                new GenesysTranscriptMessageDto("Customer", Start.AddMinutes(1), "I need a human", ExternalMessageId: "m2"),
                new GenesysTranscriptMessageDto("HumanAgent", FirstHumanLine, "Hello, this is Moaz", "Moaz Ibrahim Abou Chaar", agentGenesysId, "m3"),
            ]));

    [Fact]
    public async Task WhatsAppAcceptedAt1521_IsAssignedAndTransferredAt1521_AndRecordedAt1534()
    {
        var w = await AiConversationAsync();
        w.F.Clock.Set(EndProcessed);   // the first the integration hears of the person is the end of the conversation

        var result = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, w.Ticket.TicketId, EndWithHumanLine(w.AgentGenesysId));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        Assert.Equal(w.AgentId, w.Ticket.CurrentOwnerEmployeeId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket.CurrentDepartmentId);

        // The ownership change carries when it happened ...
        var transfer = Assert.Single(w.F.WorkflowEvents.All, e => e.TicketId == w.Ticket.TicketId && e.EventType == WorkflowEventType.TicketTransferred);
        Assert.Equal(FirstHumanLine, transfer.OccurredAtUtc);
        Assert.Equal(FirstHumanLine, Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == w.Ticket.TicketId && a.AssignedEmployeeId == w.AgentId).AssignedAtUtc);

        // ... the history keeps both clocks apart: the event's own time and when TigerCS processed it ...
        var history = Assert.Single(w.F.AssignmentHistory.Entries, h => h.Source == TicketAssignmentSource.GenesysCallTransfer);
        Assert.Equal(FirstHumanLine, history.SourceEventAtUtc);
        Assert.Equal(EndProcessed, history.ChangedAtUtc);

        // ... and the audit row says so explicitly.
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer"
            && e.AfterValue!.Contains($"OccurredAtUtc={FirstHumanLine:O}", StringComparison.Ordinal)
            && e.AfterValue.Contains($"RecordedAtUtc={EndProcessed:O}", StringComparison.Ordinal));

        // First Response is the actual first human reply, not the end of the conversation.
        Assert.Equal(FirstHumanLine, w.Ticket.FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task AWhatsAppTransfer_IsNeverWordedAsAVoiceCall()
    {
        var w = await AiConversationAsync();
        w.F.Clock.Set(EndProcessed);

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, w.Ticket.TicketId, EndWithHumanLine(w.AgentGenesysId));

        var transferAudit = Assert.Single(w.F.Audit.Entries, e => e.Action == "Transfer" && e.AfterValue!.Contains("Source=Transcript", StringComparison.Ordinal));
        Assert.Contains("WhatsApp conversation accepted by a Call Center agent", transferAudit.AfterValue);
        Assert.DoesNotContain("Call answered", transferAudit.AfterValue);

        var note = w.F.WorkflowEvents.All.Single(e => e.EventType == WorkflowEventType.TicketTransferred).Note;
        Assert.Contains("WhatsApp conversation accepted", note);
        Assert.DoesNotContain("Call answered", note);

        // AI -> human: the AI Agent was the previous assignee, so this is the "next human" entry, worded for the channel.
        Assert.Equal("Genesys whatsapp conversation transfer: the next human agent accepted", w.F.AssignmentHistory.Entries.Single(h => h.Source == TicketAssignmentSource.GenesysCallTransfer).Reason);
    }

    [Fact]
    public async Task AVoiceCall_KeepsTheCallWording()
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        f.AddAgent("g-voice", "Voice Agent");

        var created = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto("conv-call", GenesysChannel.Phone, CustomerPhone: "+971500000002", DepartmentId: cs.DepartmentId, AgentId: "g-voice"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, created.Outcome);
        Assert.Equal("Genesys direct call: answered by the human agent", f.AssignmentHistory.Entries.Single(h => h.Source == TicketAssignmentSource.GenesysDirectCall).Reason);
    }

    // ---- duplicates, replays and stale events ----

    [Fact]
    public async Task ReplayingTheSameEnd_WritesNoSecondAssignment_NoSecondTransfer_AndNoSecondNotification()
    {
        var w = await AiConversationAsync();
        w.F.Clock.Set(EndProcessed);
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, w.Ticket.TicketId, EndWithHumanLine(w.AgentGenesysId));

        int Notifications() => w.F.Outbox.Committed.Concat(w.F.Outbox.Staged)
            .Count(m => m.EventType is OutboxEventTypes.TicketAssigned or OutboxEventTypes.TicketAssignedToAgent);
        var notifications = Notifications();
        Assert.True(notifications >= 1, "the genuine ownership change is announced");
        var assignments = w.F.TicketAssignments.Added.Count;
        var transfers = w.F.WorkflowEvents.All.Count(e => e.EventType == WorkflowEventType.TicketTransferred);
        var history = w.F.AssignmentHistory.Entries.Count;

        w.F.Clock.Advance(TimeSpan.FromMinutes(5));
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, w.Ticket.TicketId, EndWithHumanLine(w.AgentGenesysId));

        Assert.Equal(assignments, w.F.TicketAssignments.Added.Count);
        Assert.Equal(transfers, w.F.WorkflowEvents.All.Count(e => e.EventType == WorkflowEventType.TicketTransferred));
        Assert.Equal(history, w.F.AssignmentHistory.Entries.Count);
        Assert.Equal(notifications, Notifications());
        Assert.Equal(w.AgentId, w.Ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task ADelayedAiOnlyEvent_AfterAHumanAcceptedTheConversation_DoesNotTakeOwnershipBack()
    {
        var w = await AiConversationAsync();
        w.F.Clock.Set(EndProcessed);
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, w.Ticket.TicketId, EndWithHumanLine(w.AgentGenesysId));

        // A late AI-only transcript and an unconnected queue change arrive afterwards.
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, w.Ticket.TicketId, new GenesysTicketUpdateDto(
            "conv-wa", Routing: new GenesysRoutingUpdateDto(QueueId: "q-ai", QueueName: "AI Queue"),
            Transcript: [new GenesysTranscriptMessageDto("VirtualAgent", Start.AddSeconds(30), "Still here?", "Bot", ExternalMessageId: "late-ai")]));

        Assert.Equal(w.AgentId, w.Ticket.CurrentOwnerEmployeeId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket.CurrentDepartmentId);
    }

    [Fact]
    public async Task AnAgentWhoWasOnlyOffered_IsNeverRecordedAsHavingAcceptedIt()
    {
        var w = await AiConversationAsync();
        w.F.AddAgent("g-offered", "Offered Agent");

        // An offer is a handoff naming an assigned agent; it is not an acceptance. Any owner is the Round Robin provisional one, never "answered".
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, w.Ticket.TicketId, new GenesysTicketUpdateDto(
            "conv-wa", AgentId: "g-offered", Handoff: new GenesysHandoffUpdateDto(Required: true, AgentAvailable: true, Trigger: "CustomerRequestedHuman", Reason: "asked")));

        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer");
        Assert.DoesNotContain(w.F.AssignmentHistory.Entries, h => h.Source is TicketAssignmentSource.GenesysDirectCall or TicketAssignmentSource.GenesysCallTransfer);
    }
}
