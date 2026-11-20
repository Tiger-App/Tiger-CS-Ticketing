using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.GenesysIntegration.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The live (incremental) transcript: messages delivered through <c>PATCH /api/genesys/tickets/{id}</c> while the conversation is still going
/// on. Additive and idempotent — retries and the final <c>ended.transcript</c> never duplicate or overwrite — and a human who picks the
/// interaction up reads the earlier AI conversation before replying. Bot lines never satisfy First Response; the first human line does.
/// </summary>
public class GenesysLiveTranscriptTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private static readonly DateTime T0 = DateTime.UtcNow.AddMinutes(-30);

    private sealed record World(GenesysServiceFixture F, Department Arrival, Department Leasing, RequestType Handover, Ticket Ticket);

    private static async Task<World> BuildAsync(string conversationId = "conv-live")
    {
        var f = new GenesysServiceFixture();
        var (arrival, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var leasing = f.Departments.AddDepartment("Leasing", "LCS");
        var template = f.WorkflowTemplates.Add(TestWorkflows.PublishedStandard(workflowId: 300));
        var handover = f.RequestTypes.Add(new RequestType(
            leasing.DepartmentId, "Handover", template.WorkflowId, (byte)PriorityLevel.Medium, false, true, true, true));
        var created = await f.Ingestion.IngestAsync(ServiceAccount, new GenesysInquiryDto(
            conversationId, GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", CustomerName: "Ahmed Ali",
            DepartmentId: arrival.DepartmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, created.Outcome);
        return new World(f, arrival, leasing, handover, f.Tickets.All.Single());
    }

    private static GenesysTranscriptMessageDto Customer(string body, int minute, string? id = null) =>
        new("Customer", T0.AddMinutes(minute), body, SenderName: "Ahmed Ali", ExternalMessageId: id);

    private static GenesysTranscriptMessageDto Bot(string body, int minute, string? id = null) =>
        new("VirtualAgent", T0.AddMinutes(minute), body, SenderName: "Tiger Bot", SenderId: "bot-1", ExternalMessageId: id);

    private static GenesysTranscriptMessageDto Human(string body, int minute, string? id = null) =>
        new("HumanAgent", T0.AddMinutes(minute), body, SenderName: "Layla", SenderId: "ga-7", ExternalMessageId: id);

    private static Task<GenesysTicketUpdateResult> Send(World w, GenesysTicketUpdateDto dto) =>
        w.F.TicketUpdate.UpdateAsync(ServiceAccount, w.Ticket.TicketId, dto);

    private static GenesysTicketUpdateDto Live(string conversationId, params GenesysTranscriptMessageDto[] messages) =>
        new(conversationId, Transcript: messages);

    private static IReadOnlyList<TicketInteractionMessage> Stored(World w, string conversationId = "conv-live") =>
        w.F.Conversations.AllMessages
            .Where(m => m.TicketInteractionId == w.F.InteractionFor(conversationId)!.TicketInteractionId)
            .OrderBy(m => m.Sequence).ToList();

    // ---- incremental delivery ----

    [Fact]
    public async Task LiveMessages_AreStoredInOrder_WithTheirSenderIdentitiesAndTimestamps_WithoutEndingTheConversation()
    {
        var w = await BuildAsync();

        var first = await Send(w, Live("conv-live", Customer("How does handover work?", 1, "m-1"), Bot("After the final payment.", 2, "m-2")));
        var second = await Send(w, Live("conv-live", Customer("Thanks", 3, "m-3")));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, first.Outcome);
        Assert.Equal(2, first.TranscriptMessageCount);
        Assert.Equal(3, second.TranscriptMessageCount);
        Assert.False(second.ConversationEnded);
        Assert.False(w.F.InteractionFor("conv-live")!.IsEnded);

        var rows = Stored(w);
        Assert.Equal([1, 2, 3], rows.Select(r => r.Sequence));
        Assert.Equal(
            [InteractionMessageSender.Customer, InteractionMessageSender.VirtualAgent, InteractionMessageSender.Customer],
            rows.Select(r => r.Sender));
        Assert.Equal("Tiger Bot", rows[1].SenderName);
        Assert.Equal("bot-1", rows[1].SenderId);
        Assert.Equal(T0.AddMinutes(2), rows[1].SentAtUtc);       // the real time, not the delivery time
        Assert.Equal("m-2", rows[1].ExternalMessageId);
        Assert.Equal(TicketStatus.Open, w.Ticket.TicketStatus);
        Assert.Empty(w.F.Handoffs.All);                           // a transcript is never a handoff signal
    }

    [Fact]
    public async Task ARetriedDelivery_StoresNothingTwice_AndWritesNoSecondAuditEntry()
    {
        var w = await BuildAsync();
        var batch = Live("conv-live", Customer("hi", 1, "m-1"), Bot("hello", 2, "m-2"));

        await Send(w, batch);
        var audits = w.F.Audit.Entries.Count(e => e.Action == GenesysAuditActions.TranscriptAppended);
        var again = await Send(w, batch);

        Assert.Equal(2, again.TranscriptMessageCount);
        Assert.Equal(2, Stored(w).Count);
        Assert.Equal(audits, w.F.Audit.Entries.Count(e => e.Action == GenesysAuditActions.TranscriptAppended));
    }

    [Fact]
    public async Task AnIdLongerThanTheStoredLengthOrPaddedWithSpaces_IsStillRecognisedOnRetry()
    {
        var w = await BuildAsync();
        var longId = new string('x', TicketInteractionMessage.ExternalMessageIdMaxLength + 10);

        await Send(w, Live("conv-live", Customer("long", 1, longId), Customer("padded", 2, "  m-2  ")));
        await Send(w, Live("conv-live", Customer("long", 1, longId), Customer("padded", 2, "m-2")));

        Assert.Equal(["long", "padded"], Stored(w).Select(r => r.Body));
    }

    [Fact]
    public async Task AnOverlappingBatch_AppendsOnlyTheNewMessages_ContinuingTheSequence()
    {
        var w = await BuildAsync();
        await Send(w, Live("conv-live", Customer("a", 1, "m-1"), Bot("b", 2, "m-2")));

        await Send(w, Live("conv-live", Bot("b", 2, "m-2"), Customer("c", 3, "m-3"), Bot("d", 4, "m-4")));

        Assert.Equal(["a", "b", "c", "d"], Stored(w).Select(r => r.Body));
        Assert.Equal([1, 2, 3, 4], Stored(w).Select(r => r.Sequence));
    }

    [Fact]
    public async Task MessagesWithoutIds_AreDeduplicatedOnSenderTimeAndBody()
    {
        var w = await BuildAsync();

        await Send(w, Live("conv-live", Customer("hi", 1), Bot("hello", 2)));
        await Send(w, Live("conv-live", Customer("hi", 1), Bot("hello", 2), Customer("again", 3)));

        Assert.Equal(["hi", "hello", "again"], Stored(w).Select(r => r.Body));
    }

    // ---- final transcript reconciliation ----

    [Fact]
    public async Task TheFinalTranscript_RepeatingLiveMessages_DuplicatesAndOverwritesNothing_AndAddsOnlyWhatIsNew()
    {
        var w = await BuildAsync();
        await Send(w, Live("conv-live", Customer("hi", 1, "m-1"), Bot("hello", 2, "m-2")));
        var before = Stored(w).Select(r => r.TicketInteractionMessageId).ToList();
        var endedAt = T0.AddMinutes(10);

        var ended = await Send(w, new GenesysTicketUpdateDto("conv-live", Ended: new GenesysConversationEndUpdateDto(endedAt, "CustomerDisconnect",
        [
            Customer("hi", 1, "m-1"), Bot("hello", 2, "m-2"), Customer("bye", 9, "m-3")
        ])));

        Assert.True(ended.ConversationEnded);
        Assert.Equal(3, ended.TranscriptMessageCount);
        var rows = Stored(w);
        Assert.Equal(["hi", "hello", "bye"], rows.Select(r => r.Body));
        Assert.Equal(before, rows.Take(2).Select(r => r.TicketInteractionMessageId));   // the live rows are the same rows
        Assert.Equal(endedAt, w.F.InteractionFor("conv-live")!.EndedAtUtc);
    }

    [Fact]
    public async Task AFinalTranscript_ThatNowCarriesIdsForLinesFirstSentWithoutThem_DoesNotDuplicateThem()
    {
        var w = await BuildAsync();
        await Send(w, Live("conv-live", Customer("hi", 1), Bot("hello", 2)));

        await Send(w, new GenesysTicketUpdateDto("conv-live", Ended: new GenesysConversationEndUpdateDto(T0.AddMinutes(5), "Done",
            [Customer("hi", 1, "m-1"), Bot("hello", 2, "m-2")])));

        Assert.Equal(2, Stored(w).Count);
    }

    [Fact]
    public async Task LiveMessagesDeliveredAfterTheEnd_AreStillAppended_AndTheRecordedEndIsNotMoved()
    {
        var w = await BuildAsync();
        var endedAt = T0.AddMinutes(5);
        await Send(w, new GenesysTicketUpdateDto("conv-live", Ended: new GenesysConversationEndUpdateDto(endedAt, "Done", [Customer("hi", 1, "m-1")])));

        await Send(w, Live("conv-live", Bot("late farewell", 6, "m-2")));

        Assert.Equal(2, Stored(w).Count);
        Assert.Equal(endedAt, w.F.InteractionFor("conv-live")!.EndedAtUtc);
    }

    // ---- First Response ----

    [Fact]
    public async Task BotMessages_NeverRecordFirstResponse_TheFirstHumanMessageDoes_AtItsOwnTimestamp()
    {
        var w = await BuildAsync();

        await Send(w, Live("conv-live", Customer("hi", 1, "m-1"), Bot("hello", 2, "m-2"), Bot("one moment", 3, "m-3")));
        Assert.Null(w.Ticket.FirstHumanResponseAtUtc);

        await Send(w, Live("conv-live", Human("Hello, this is Layla", 8, "m-4")));
        Assert.Equal(T0.AddMinutes(8), w.Ticket.FirstHumanResponseAtUtc);

        // Write-once: a later human line, or a replay, moves nothing.
        await Send(w, Live("conv-live", Human("Anything else?", 12, "m-5")));
        await Send(w, Live("conv-live", Human("Hello, this is Layla", 8, "m-4")));
        Assert.Equal(T0.AddMinutes(8), w.Ticket.FirstHumanResponseAtUtc);
    }

    // ---- a human sees the AI conversation first ----

    [Fact]
    public async Task AHumanTakingTheInteraction_FindsTheEarlierAiConversationAlreadyStored_BeforeTheyReply()
    {
        var w = await BuildAsync();
        await Send(w, Live("conv-live", Customer("I want to talk to someone", 1, "m-1"), Bot("Connecting you.", 2, "m-2")));

        await Send(w, new GenesysTicketUpdateDto("conv-live", Handoff: new GenesysHandoffUpdateDto(Required: true, Trigger: "CustomerRequestedHuman")));
        var view = await w.F.InteractionQuery.GetForTicketAsync(Guid.NewGuid(), [Roles.SystemAdministrator], w.Ticket.TicketId);

        var interaction = Assert.Single(view.Response!.Interactions);
        Assert.False(interaction.EndedAtUtc.HasValue);                              // still live
        Assert.Equal(["Customer", "VirtualAgent"], interaction.Messages.Select(m => m.Sender));
        Assert.Equal("WaitingForHuman", interaction.HandlingState);
        Assert.Null(w.Ticket.FirstHumanResponseAtUtc);
    }

    // ---- validation, no partial writes, unknown conversation ----

    [Fact]
    public async Task AMalformedLiveMessage_IsRefusedWhole_AndNothingElseInTheUpdateIsApplied()
    {
        var w = await BuildAsync();

        var result = await Send(w, new GenesysTicketUpdateDto(
            "conv-live",
            RequestType: new GenesysRequestTypeDto(w.Handover.RequestTypeId),
            Transcript: [Customer("fine", 1, "m-1"), new GenesysTranscriptMessageDto("Robot", T0, "who?")]));

        Assert.Equal(GenesysTicketUpdateOutcome.InvalidTranscript, result.Outcome);
        Assert.Empty(Stored(w));
        Assert.Null(w.Ticket.RequestTypeId);
    }

    [Fact]
    public async Task AnEmptyBody_IsRefused_AndAnEmptyTranscriptChangesNothing()
    {
        var w = await BuildAsync();

        var bad = await Send(w, Live("conv-live", new GenesysTranscriptMessageDto("Customer", T0, "  ")));
        var empty = await Send(w, new GenesysTicketUpdateDto("conv-live", Transcript: []));

        Assert.Equal(GenesysTicketUpdateOutcome.InvalidTranscript, bad.Outcome);
        Assert.Equal(GenesysTicketUpdateOutcome.Applied, empty.Outcome);
        Assert.Empty(Stored(w));
    }

    [Fact]
    public async Task ATranscriptForAConversationOfAnotherTicket_IsRefused_AndStoresNothing()
    {
        var w = await BuildAsync();
        await w.F.Ingestion.IngestAsync(ServiceAccount, new GenesysInquiryDto(
            "conv-other", GenesysChannel.WebsiteChat, CustomerPhone: "+971500000002", DepartmentId: w.Arrival.DepartmentId));

        var result = await Send(w, Live("conv-other", Customer("wrong ticket", 1, "m-1")));

        Assert.Equal(GenesysTicketUpdateOutcome.ConversationTicketMismatch, result.Outcome);
        Assert.Empty(w.F.Conversations.AllMessages);
    }

    // ---- classification and SLA through the same update ----

    [Fact]
    public async Task ClassificationInTheSameUpdate_RoutesToTheRequestTypesDepartment_AssignsAndAdjustsTheSlaWithoutRestartingTheClock()
    {
        var w = await BuildAsync();
        var employee = Guid.NewGuid();
        w.F.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(employee, w.Leasing.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        w.F.AssignmentRules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(w.Handover.RequestTypeId, employee));
        await w.F.Sla.RequestTypeSla.AddAsync(new RequestTypeSlaPolicy(
            w.Handover.RequestTypeId, (byte)PriorityLevel.Medium, SlaTriggerType.TicketCreated, SlaDurationUnit.Hours,
            null, null, 8, null, clockBasis: SlaClockBasis.TwentyFourSeven));
        var period = Assert.Single(w.F.Sla.SlaInstances.All);
        var startedAt = period.PeriodStartAtUtc;

        var result = await Send(w, new GenesysTicketUpdateDto(
            "conv-live",
            RequestType: new GenesysRequestTypeDto(Name: "Handover"),
            Transcript: [Customer("When can I collect my keys?", 1, "m-1"), Bot("Let me check handover.", 2, "m-2")]));

        Assert.Equal("Classified", result.Classification!.Status);
        Assert.Equal(w.Leasing.DepartmentId, w.Ticket.CurrentDepartmentId);
        Assert.Equal(w.Arrival.DepartmentId, w.Ticket.OriginatingDepartmentId);
        Assert.Equal(employee, w.Ticket.CurrentOwnerEmployeeId);
        Assert.Same(period, Assert.Single(w.F.Sla.SlaInstances.All));
        Assert.Equal(startedAt, period.PeriodStartAtUtc);
        Assert.Equal(startedAt.AddHours(8), period.ResolutionDueAtUtc);
        Assert.Equal(2, result.TranscriptMessageCount);
        Assert.Empty(w.F.Handoffs.All);
        Assert.Null(w.Ticket.FirstHumanResponseAtUtc);   // being assigned to an employee is not a response either
    }
}
