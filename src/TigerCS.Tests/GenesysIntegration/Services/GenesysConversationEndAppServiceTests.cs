using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Ending a conversation, preserving its transcript — and the phase's
/// load-bearing rule that <b>chat end is not ticket closed</b>.
/// </summary>
public class GenesysConversationEndAppServiceTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private static readonly DateTime ChatStart = new(2026, 9, 10, 9, 32, 0, DateTimeKind.Utc);

    /// <summary>Ingests one website chat and returns the fixture plus the created ticket id.</summary>
    private static async Task<(GenesysServiceFixture Fixture, long TicketId)> SeedChatAsync(string conversationId)
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(
                conversationId, GenesysChannel.WebsiteChat,
                CustomerPhone: "+971500000001", CustomerName: "Ahmed Ali",
                AgentName: "John", DepartmentId: department.DepartmentId, StartedAtUtc: ChatStart));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return (f, result.Ticket!.TicketId);
    }

    private static GenesysTranscriptMessageDto Message(string sender, int minute, string body, string? senderName = null) =>
        new(sender, ChatStart.AddMinutes(minute), body, senderName);

    // ---- 11. A chat conversation can be ended ----

    [Fact]
    public async Task End_RecordsTheEndTimeAndReason()
    {
        var (f, ticketId) = await SeedChatAsync("conv-end-1");

        var endedAt = ChatStart.AddMinutes(15);
        var result = await f.ConversationEnd.EndAsync(
            ServiceAccount, new GenesysConversationEndDto("conv-end-1", endedAt, "AgentDisconnect"));

        Assert.Equal(GenesysConversationEndOutcome.Ended, result.Outcome);
        Assert.Equal(ticketId, result.TicketId);

        var interaction = f.InteractionFor("conv-end-1")!;
        Assert.True(interaction.IsEnded);
        Assert.Equal(endedAt, interaction.EndedAtUtc);
        Assert.Equal("AgentDisconnect", interaction.EndReason);
        Assert.Contains(f.Audit.Written, w => w.Action == "GenesysConversationEnded" && w.EntityId == "conv-end-1");
    }

    // ---- 12. Ending the chat does NOT close the ticket ----

    [Fact]
    public async Task End_DoesNotCloseOrOtherwiseChangeTheTicket()
    {
        // The NOC case: the chat ends, the workflow runs for days.
        var (f, ticketId) = await SeedChatAsync("conv-noc");
        var ticket = f.Tickets.All.Single(t => t.TicketId == ticketId);
        var statusBefore = ticket.TicketStatus;
        var slaBefore = ticket.SlaState;
        var statusHistoryBefore = f.StatusHistory.Added.Count;

        var result = await f.ConversationEnd.EndAsync(
            ServiceAccount,
            new GenesysConversationEndDto(
                "conv-noc", ChatStart.AddMinutes(15), "CustomerDisconnect",
                Transcript: [Message("Customer", 0, "I need an NOC for my unit.")]));

        Assert.Equal(GenesysConversationEndOutcome.Ended, result.Outcome);

        // The interaction ended; the TICKET did not move at all.
        Assert.True(f.InteractionFor("conv-noc")!.IsEnded);
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
        Assert.Equal(statusBefore, ticket.TicketStatus);
        Assert.Equal(slaBefore, ticket.SlaState);
        Assert.Null(ticket.ResolutionOutcome);
        Assert.Equal(statusHistoryBefore, f.StatusHistory.Added.Count);

        // And the response says so, so the caller can see it too.
        Assert.Equal(nameof(TicketStatus.Open), result.TicketStatus);
    }

    // ---- 13 & 14. The transcript: attached to the right conversation, in order ----

    [Fact]
    public async Task End_StoresTheTranscript_AgainstTheCorrectConversationAndTicket()
    {
        var (f, ticketId) = await SeedChatAsync("conv-transcript");
        // A second, unrelated conversation on another ticket — the transcript
        // must not leak onto it.
        var otherDepartment = f.SeedGenesysDepartment("Leasing", "LS");
        await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(
                "conv-other", GenesysChannel.WhatsApp,
                DepartmentId: otherDepartment.Department.DepartmentId));

        await f.ConversationEnd.EndAsync(
            ServiceAccount,
            new GenesysConversationEndDto(
                "conv-transcript", ChatStart.AddMinutes(15), "Completed",
                Transcript:
                [
                    Message("Customer", 0, "Hello, I need an update about my unit.", "Ahmed Ali"),
                    Message("HumanAgent", 1, "Sure. Can you confirm your unit number?", "John")
                ]));

        var interaction = f.InteractionFor("conv-transcript")!;
        var stored = await f.Conversations.ListMessagesAsync(interaction.TicketInteractionId);

        Assert.Equal(2, stored.Count);
        Assert.All(stored, m => Assert.Equal(interaction.TicketInteractionId, m.TicketInteractionId));
        Assert.Equal(ticketId, interaction.TicketId);

        // The other conversation has none of it.
        var other = f.InteractionFor("conv-other")!;
        Assert.Empty(await f.Conversations.ListMessagesAsync(other.TicketInteractionId));
    }

    [Fact]
    public async Task End_MultipleMessages_PreserveTheirChronologicalOrder()
    {
        var (f, _) = await SeedChatAsync("conv-order");

        await f.ConversationEnd.EndAsync(
            ServiceAccount,
            new GenesysConversationEndDto(
                "conv-order", ChatStart.AddMinutes(15), "Completed",
                Transcript:
                [
                    Message("Customer", 0, "Hello, I need an update about my unit."),
                    Message("HumanAgent", 1, "Sure. Can you confirm your unit number?"),
                    Message("Customer", 2, "1204"),
                    Message("HumanAgent", 3, "Thank you, checking now.")
                ]));

        var interaction = f.InteractionFor("conv-order")!;
        var stored = await f.Conversations.ListMessagesAsync(interaction.TicketInteractionId);

        Assert.Equal([1, 2, 3, 4], stored.Select(m => m.Sequence));
        Assert.Equal(
            ["Hello, I need an update about my unit.", "Sure. Can you confirm your unit number?", "1204", "Thank you, checking now."],
            stored.Select(m => m.Body));
        Assert.Equal(
            [InteractionMessageSender.Customer, InteractionMessageSender.HumanAgent,
             InteractionMessageSender.Customer, InteractionMessageSender.HumanAgent],
            stored.Select(m => m.Sender));
        Assert.True(stored.Zip(stored.Skip(1)).All(pair => pair.First.SentAtUtc <= pair.Second.SentAtUtc));
    }

    // ---- 15. A duplicate end event is idempotent ----

    [Fact]
    public async Task End_DeliveredTwice_IsIdempotent_AndNeverDuplicatesTheTranscript()
    {
        var (f, _) = await SeedChatAsync("conv-dup-end");
        var firstEndedAt = ChatStart.AddMinutes(15);
        GenesysConversationEndDto EndRequest(DateTime endedAt) =>
            new("conv-dup-end", endedAt, "AgentDisconnect",
                Transcript: [Message("Customer", 0, "Hello"), Message("HumanAgent", 1, "Hi, how can I help?")]);

        var first = await f.ConversationEnd.EndAsync(ServiceAccount, EndRequest(firstEndedAt));
        // The redelivery even carries a LATER end time — it must not move the
        // recorded one.
        var second = await f.ConversationEnd.EndAsync(ServiceAccount, EndRequest(firstEndedAt.AddMinutes(30)));

        Assert.Equal(GenesysConversationEndOutcome.Ended, first.Outcome);
        Assert.Equal(GenesysConversationEndOutcome.AlreadyEnded, second.Outcome);

        var interaction = f.InteractionFor("conv-dup-end")!;
        Assert.Equal(firstEndedAt, interaction.EndedAtUtc);
        Assert.Equal(2, (await f.Conversations.ListMessagesAsync(interaction.TicketInteractionId)).Count);
        Assert.Equal(2, second.TranscriptMessageCount);
    }

    // ---- 8 (interrupted chat): whatever is available is still preserved ----

    [Fact]
    public async Task End_InterruptedChat_StoresWhateverWasSaidUpToThatMoment()
    {
        // The customer closed the browser mid-sentence: no end reason from
        // Genesys, a partial transcript, and the ticket must remain.
        var (f, ticketId) = await SeedChatAsync("conv-interrupted");

        var result = await f.ConversationEnd.EndAsync(
            ServiceAccount,
            new GenesysConversationEndDto(
                "conv-interrupted", ChatStart.AddMinutes(4), EndReason: null,
                Transcript: [Message("Customer", 0, "Hi, my AC is not"), Message("System", 1, "Customer disconnected.")]));

        Assert.Equal(GenesysConversationEndOutcome.Ended, result.Outcome);
        var interaction = f.InteractionFor("conv-interrupted")!;
        Assert.True(interaction.IsEnded);
        Assert.Null(interaction.EndReason);
        Assert.Equal(2, (await f.Conversations.ListMessagesAsync(interaction.TicketInteractionId)).Count);

        // The ticket is still there, still open, still workable.
        var ticket = f.Tickets.All.Single(t => t.TicketId == ticketId);
        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
    }

    [Fact]
    public async Task End_WithNoTranscriptAtAll_StillFinalizesTheInteraction()
    {
        // A voice call: nothing to transcribe, but the interaction must still
        // be finalized.
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto("conv-voice", GenesysChannel.Phone,
                CustomerPhone: "+971500000001", DepartmentId: department.DepartmentId));

        var result = await f.ConversationEnd.EndAsync(
            ServiceAccount, new GenesysConversationEndDto("conv-voice", ChatStart.AddMinutes(6), "AgentDisconnect"));

        Assert.Equal(GenesysConversationEndOutcome.Ended, result.Outcome);
        Assert.Equal(0, result.TranscriptMessageCount);
        Assert.True(f.InteractionFor("conv-voice")!.IsEnded);
    }

    [Fact]
    public async Task End_FillsInTheAgentWhenTheStartEventDidNotNameOne()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto("conv-lateagent", GenesysChannel.WhatsApp,
                DepartmentId: department.DepartmentId));
        Assert.Null(f.InteractionFor("conv-lateagent")!.GenesysAgentName);

        await f.ConversationEnd.EndAsync(
            ServiceAccount,
            new GenesysConversationEndDto("conv-lateagent", ChatStart, "Completed", AgentId: "ga-7", AgentName: "John"));

        var interaction = f.InteractionFor("conv-lateagent")!;
        Assert.Equal("John", interaction.GenesysAgentName);
        Assert.Equal("ga-7", interaction.GenesysAgentId);
    }

    // ---- Failure modes ----

    [Fact]
    public async Task End_UnknownConversation_IsReportedNotFound()
    {
        // An end for a call that rang and was never answered — it produced no
        // ticket, so there is no interaction to finalize.
        var f = new GenesysServiceFixture();

        var result = await f.ConversationEnd.EndAsync(
            ServiceAccount, new GenesysConversationEndDto("conv-never-ingested", ChatStart, "Abandoned"));

        Assert.Equal(GenesysConversationEndOutcome.ConversationNotFound, result.Outcome);
    }

    [Fact]
    public async Task End_MalformedTranscript_StoresNothingAtAll_AndLeavesTheInteractionOpen()
    {
        var (f, _) = await SeedChatAsync("conv-badtranscript");

        var result = await f.ConversationEnd.EndAsync(
            ServiceAccount,
            new GenesysConversationEndDto(
                "conv-badtranscript", ChatStart.AddMinutes(5), "Completed",
                Transcript:
                [
                    Message("Customer", 0, "Hello"),
                    Message("Supervisor", 1, "Not a sender this system recognizes")
                ]));

        Assert.Equal(GenesysConversationEndOutcome.InvalidTranscript, result.Outcome);

        // All-or-nothing: validated before any write, so the valid first
        // message was not stored either, and the interaction can be re-ended
        // once the caller resends a correct transcript.
        var interaction = f.InteractionFor("conv-badtranscript")!;
        Assert.False(interaction.IsEnded);
        Assert.Empty(await f.Conversations.ListMessagesAsync(interaction.TicketInteractionId));
    }

    [Fact]
    public async Task End_IntegrationDisabled_WritesNothing()
    {
        var (f, _) = await SeedChatAsync("conv-flagoff");
        f.Options.Enabled = false;

        var result = await f.ConversationEnd.EndAsync(
            ServiceAccount, new GenesysConversationEndDto("conv-flagoff", ChatStart, "AgentDisconnect"));

        Assert.Equal(GenesysConversationEndOutcome.IntegrationDisabled, result.Outcome);
        Assert.False(f.InteractionFor("conv-flagoff")!.IsEnded);
    }

    // ---- The Ticket Details read model ----

    [Fact]
    public async Task InteractionHistory_ShowsTheConversationWithItsTranscript_AndIndependentStatus()
    {
        var (f, ticketId) = await SeedChatAsync("conv-history");
        var viewer = Guid.NewGuid();

        await f.ConversationEnd.EndAsync(
            ServiceAccount,
            new GenesysConversationEndDto(
                "conv-history", ChatStart.AddMinutes(15), "Completed",
                Transcript:
                [
                    Message("Customer", 0, "Hello, I need an update about my unit.", "Ahmed Ali"),
                    Message("HumanAgent", 1, "Sure. Can you confirm your unit number?", "John"),
                    Message("Customer", 2, "1204", "Ahmed Ali")
                ]));

        f.Tickets.All.Single(t => t.TicketId == ticketId).AssignTo(viewer); // a CS Agent sees only a ticket assigned to them
        var view = await f.InteractionQuery.GetForTicketAsync(viewer, [Roles.CsAgent], ticketId);

        Assert.Equal(TicketQueryOutcome.Success, view.Outcome);
        var interaction = Assert.Single(view.Response!.Interactions);
        Assert.Equal("Live Chat", interaction.ChannelName);
        Assert.Equal("Ended", interaction.Status);
        Assert.Equal("Completed", interaction.EndReason);
        Assert.Equal(ChatStart, interaction.StartedAtUtc);
        Assert.Equal(3, interaction.Messages.Count);
        Assert.Equal(["Customer", "HumanAgent", "Customer"], interaction.Messages.Select(m => m.Sender));
        Assert.Equal("1204", interaction.Messages[2].Body);

        // The ticket is untouched by the conversation ending.
        Assert.Equal(TicketStatus.Open, f.Tickets.All.Single(t => t.TicketId == ticketId).TicketStatus);
    }

    [Fact]
    public async Task InteractionHistory_MultipleInteractions_AreReturnedChronologically()
    {
        var (f, ticketId) = await SeedChatAsync("conv-first");
        var viewer = Guid.NewGuid();

        // A later follow-up call on the same ticket.
        await f.Interactions.AddAsync(TicketInteraction.CreateFromGenesys(
            ticketId, WellKnownChannels.Phone, "+971500000001",
            genesysConversationId: "conv-second", calledNumber: null, genesysQueueId: null, genesysQueueName: null,
            genesysAgentId: null, genesysAgentName: "Layla",
            interactionStartedAtUtc: ChatStart.AddDays(1), direction: "Inbound", ChatStart.AddDays(1)));

        f.Tickets.All.Single(t => t.TicketId == ticketId).AssignTo(viewer); // a CS Agent sees only a ticket assigned to them
        var view = await f.InteractionQuery.GetForTicketAsync(viewer, [Roles.CsAgent], ticketId);

        Assert.Equal(TicketQueryOutcome.Success, view.Outcome);
        Assert.Equal(
            ["conv-first", "conv-second"],
            view.Response!.Interactions.Select(i => i.GenesysConversationId));
        Assert.True(view.Response.Interactions[0].StartedAtUtc < view.Response.Interactions[1].StartedAtUtc);
    }

    [Fact]
    public async Task InteractionHistory_IsScopedToTheSameDepartmentVisibilityAsTheTicket()
    {
        var (f, ticketId) = await SeedChatAsync("conv-scoped");

        // A Department Employee of another department cannot see the ticket,
        // so cannot see its conversations either.
        var outsider = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(outsider, 999, isPrimary: true, DateTime.UtcNow, null));

        var view = await f.InteractionQuery.GetForTicketAsync(outsider, [Roles.DepartmentEmployee], ticketId);

        Assert.Equal(TicketQueryOutcome.Forbidden, view.Outcome);
        Assert.Null(view.Response);
    }
}
