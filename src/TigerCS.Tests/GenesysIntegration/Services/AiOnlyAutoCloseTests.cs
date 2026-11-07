using TigerCS.Application.Modules.Notifications.Dto;
using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The AI-only 18-hour closure: the strict threshold (measured from ticket creation, UTC), what counts as "AI only" (stored facts, not display
/// text), every kind of human involvement leaving the ticket alone, tickets already closed, and a re-run being a no-op.
/// Runs the REAL ingestion, update and lifecycle services over fakes with a controllable clock.
/// </summary>
public class AiOnlyAutoCloseTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private const string Phone = "+971500000001";
    private static readonly TimeSpan Threshold = TimeSpan.FromHours(18);

    private static async Task<(GenesysServiceFixture F, long TicketId, string Conv)> IngestAsync(
        string conv = "conv-ai-only", GenesysChannel channel = GenesysChannel.WebsiteChat)
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(conv, channel, CustomerPhone: Phone, DepartmentId: department.DepartmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return (f, result.Ticket!.TicketId, conv);
    }

    /// <summary>The job body for one batch: list candidates, then re-check and close each.</summary>
    private static async Task<List<AiOnlyAutoCloseOutcome>> RunJobAsync(GenesysServiceFixture f)
    {
        var outcomes = new List<AiOnlyAutoCloseOutcome>();
        foreach (var id in await f.AiOnlyClose.ListCandidatesAsync(afterTicketId: 0))
        {
            outcomes.Add(await f.AiOnlyClose.TryCloseAsync(id));
        }

        return outcomes;
    }

    private static Task SayAsync(GenesysServiceFixture f, long ticketId, string conv, string sender, string id) =>
        f.ConversationEnd.AppendTranscriptAsync(
            ServiceAccount, conv, [new GenesysTranscriptMessageDto(sender, f.Clock.GetUtcNow().UtcDateTime, "hello", SenderName: sender, ExternalMessageId: id)], ticketId: ticketId);

    // ---- the 18-hour threshold ----

    [Fact]
    public async Task JustUnder18Hours_IsNotClosed()
    {
        var (f, _, _) = await IngestAsync();
        f.Clock.Advance(Threshold - TimeSpan.FromMinutes(1));

        Assert.Empty(await RunJobAsync(f));
        Assert.Equal(TicketStatus.Open, f.Tickets.All.Single().TicketStatus);
    }

    [Fact]
    public async Task Exactly18Hours_IsNotClosed_BecauseItMustBeMoreThan18()
    {
        var (f, ticketId, _) = await IngestAsync();
        f.Clock.Advance(Threshold);

        Assert.Equal(AiOnlyAutoCloseOutcome.NotEligible, await f.AiOnlyClose.TryCloseAsync(ticketId));
        Assert.Empty(await f.AiOnlyClose.ListCandidatesAsync(0));
        Assert.Equal(TicketStatus.Open, f.Tickets.All.Single().TicketStatus);
    }

    [Fact]
    public async Task Over18Hours_Closes_WithTheExactReason_ThroughTheLifecycle()
    {
        var (f, ticketId, _) = await IngestAsync();
        f.Clock.Advance(Threshold + TimeSpan.FromMinutes(1));

        Assert.Equal([AiOnlyAutoCloseOutcome.Closed], await RunJobAsync(f));

        var ticket = f.Tickets.All.Single();
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        Assert.Equal((byte)ResolutionOutcome.Resolved, ticket.ResolutionOutcome);
        Assert.NotEqual((byte)ResolutionOutcome.Cancelled, ticket.ResolutionOutcome);
        Assert.Equal("AI Only – Auto Closed After 18 Hours", AiOnlyTicketAutoCloseAppService.ClosureReason);
        Assert.Equal(AiOnlyTicketAutoCloseAppService.ClosureReason, Assert.Single(f.Resolutions.Added).ResolutionNote);

        // Open → Resolved, outcome, Resolved → Closed: system actions carrying the reason (the ticket history).
        Assert.Equal(3, f.StatusHistory.Added.Count(h => h.TicketId == ticketId && h.ActorIsSystem && h.Note == AiOnlyTicketAutoCloseAppService.ClosureReason));

        // The audit trail records the closure, with its own action so it is never mistaken for the AI's own completion.
        var audit = Assert.Single(f.Audit.Entries, e => e.Action == TicketClosureAuditActions.ClosedForAiOnlyTimeout);
        Assert.Equal(ticketId.ToString(), audit.EntityId);
        Assert.Contains($"Reason={AiOnlyTicketAutoCloseAppService.ClosureReason}", audit.AfterValue);
        Assert.Contains("Handling=AI", audit.AfterValue);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AutoCloseByAi");
    }

    [Fact]
    public async Task Closure_QueuesTheSingleClosedCustomerNotification()
    {
        var (f, ticketId, _) = await IngestAsync();
        var before = f.Outbox.Committed.Count;
        f.Clock.Advance(Threshold + TimeSpan.FromMinutes(1));

        await RunJobAsync(f);

        Assert.Equal(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
        Assert.Equal(before + 1, f.Outbox.Committed.Count);
        // One Closed event (never a Resolved one), marked as the AI-only time-limit closure so the email can explain it.
        var closed = Assert.Single(f.Outbox.Committed, m => m.EventType is "TicketClosed" or "TicketResolved");
        Assert.Equal("TicketClosed", closed.EventType);
        Assert.Equal(ClosureKinds.AiOnlyTimeout, TicketLifecycleEventPayload.FromJson(closed.Payload)!.ClosureKind);
        Assert.Contains(f.Audit.Entries, e =>
            e.Action == NotificationAuditActions.NotificationQueued && e.EntityId == ticketId.ToString()
            && e.AfterValue!.Contains("ClosureKind=AiOnlyTimeout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Closure_PreservesTheAiOnlyHandlingState()
    {
        var (f, ticketId, _) = await IngestAsync();
        f.Clock.Advance(Threshold + TimeSpan.FromMinutes(1));

        Assert.Equal(AiOnlyAutoCloseOutcome.Closed, await f.AiOnlyClose.TryCloseAsync(ticketId));

        var interaction = f.Interactions.All.Single();
        Assert.Null(interaction.HandledByUserId);
        Assert.False(interaction.RequiresHumanFollowUp);
        Assert.Null(f.Tickets.All.Single().CurrentOwnerEmployeeId);
        var history = await f.InteractionQuery.GetForTicketAsync(Guid.NewGuid(), [TigerCS.Domain.Modules.IdentityAndAccess.Roles.CsSupervisor], ticketId);
        Assert.Equal("Ai", history.Response!.Interactions.Single().HandlingState);
    }

    [Fact]
    public async Task Closure_PreservesTheTicketsInteractionsAndConversationHistory()
    {
        var (f, ticketId, conv) = await IngestAsync();
        await SayAsync(f, ticketId, conv, "Customer", "c1");
        await SayAsync(f, ticketId, conv, "VirtualAgent", "v1");
        var interactions = f.Interactions.All.Count;
        var messages = f.Conversations.AllMessages.Count;
        var number = f.Tickets.All.Single().TicketNumber;

        f.Clock.Advance(Threshold + TimeSpan.FromMinutes(1));
        await RunJobAsync(f);

        Assert.Equal(interactions, f.Interactions.All.Count);
        Assert.Equal(messages, f.Conversations.AllMessages.Count);
        Assert.Equal(number, f.Tickets.All.Single().TicketNumber);
        Assert.Equal(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
    }

    [Fact]
    public async Task ThresholdIsMeasuredFromCreation_NotFromTheLastCustomerMessage()
    {
        var (f, ticketId, conv) = await IngestAsync();
        f.Clock.Advance(TimeSpan.FromHours(17));
        await SayAsync(f, ticketId, conv, "Customer", "late");
        f.Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));

        Assert.Equal([AiOnlyAutoCloseOutcome.Closed], await RunJobAsync(f));
    }

    // ---- AI-only eligibility: transcript lines of the bot / customer / system never count as a person ----

    [Fact]
    public async Task VirtualAgentCustomerAndSystemLines_DoNotMakeItHumanHandled()
    {
        var (f, ticketId, conv) = await IngestAsync();
        await SayAsync(f, ticketId, conv, "VirtualAgent", "v1");
        await SayAsync(f, ticketId, conv, "Customer", "c1");
        await SayAsync(f, ticketId, conv, "System", "s1");
        f.Clock.Advance(Threshold + TimeSpan.FromMinutes(1));

        Assert.Equal([AiOnlyAutoCloseOutcome.Closed], await RunJobAsync(f));
    }

    [Fact]
    public async Task AgreesWithTheHandledByLabel_AiTicketsAreTheOnesTheInteractionViewCallsAi()
    {
        var (f, ticketId, _) = await IngestAsync();
        f.Clock.Advance(Threshold + TimeSpan.FromMinutes(1));

        var history = await f.InteractionQuery.GetForTicketAsync(Guid.NewGuid(), [TigerCS.Domain.Modules.IdentityAndAccess.Roles.CsSupervisor], ticketId);
        Assert.Equal("Ai", history.Response!.Interactions.Single().HandlingState);
        Assert.Equal(AiOnlyAutoCloseOutcome.Closed, await f.AiOnlyClose.TryCloseAsync(ticketId));
    }

    // ---- human involvement: never touched ----

    [Fact]
    public async Task HumanAgentTranscriptLine_LeavesTheTicketAlone()
    {
        var (f, ticketId, conv) = await IngestAsync();
        await SayAsync(f, ticketId, conv, "HumanAgent", "h1");
        f.Clock.Advance(Threshold + TimeSpan.FromMinutes(1));

        await AssertLeftAloneAsync(f, ticketId);
    }

    [Fact]
    public async Task OwnedTicket_LeavesTheTicketAlone()
    {
        var (f, ticketId, _) = await IngestAsync();
        f.Tickets.All.Single().AssignTo(Guid.NewGuid());
        f.Clock.Advance(Threshold + TimeSpan.FromMinutes(1));

        await AssertLeftAloneAsync(f, ticketId);
    }

    [Fact]
    public async Task HandledByUser_LeavesTheTicketAlone()
    {
        var (f, ticketId, conv) = await IngestAsync();
        var agent = f.MapAgent("agent-1", roles: "CS Agent");
        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId, new GenesysTicketUpdateDto(conv, Routing: new GenesysRoutingUpdateDto(AgentId: "agent-1")));
        f.Clock.Advance(Threshold + TimeSpan.FromMinutes(1));

        // Whichever fact the integration recorded (handler row, interaction handler or owner), a person is involved.
        Assert.True(agent != Guid.Empty);
        await AssertLeftAloneAsync(f, ticketId);
    }

    [Fact]
    public async Task RecordedHandler_LeavesTheTicketAlone()
    {
        var (f, ticketId, _) = await IngestAsync();
        var interaction = f.Interactions.All.Single();
        await f.Tickets.Handlers.AddIfAbsentAsync(
            TicketInteractionHandler.Create(
                interaction.TicketInteractionId, ticketId, Guid.NewGuid(), "agent-1", InteractionHandlerSource.Update, null, f.Clock.GetUtcNow().UtcDateTime));
        f.Clock.Advance(Threshold + TimeSpan.FromMinutes(1));

        await AssertLeftAloneAsync(f, ticketId);
    }

    [Fact]
    public async Task RequestedHuman_Handoff_LeavesTheTicketAlone()
    {
        var (f, ticketId, conv) = await IngestAsync();
        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId, new GenesysTicketUpdateDto(conv, Handoff: new GenesysHandoffUpdateDto(Required: true, Reason: "wants a person")));
        f.Clock.Advance(Threshold + TimeSpan.FromMinutes(1));

        await AssertLeftAloneAsync(f, ticketId);
    }

    [Fact]
    public async Task HumanFollowUpRequiredFlag_LeavesTheTicketAlone()
    {
        var (f, ticketId, conv) = await IngestAsync();
        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto(conv, HumanFollowUpRequired: true));
        f.Clock.Advance(Threshold + TimeSpan.FromMinutes(1));

        await AssertLeftAloneAsync(f, ticketId);
    }

    [Fact]
    public async Task InternalNote_WrittenByAPerson_LeavesTheTicketAlone()
    {
        var (f, ticketId, _) = await IngestAsync();
        await f.TicketNotes.AddAsync(new TicketNote(ticketId, "called the customer", Guid.NewGuid(), f.Clock.GetUtcNow().UtcDateTime));
        f.Clock.Advance(Threshold + TimeSpan.FromMinutes(1));

        await AssertLeftAloneAsync(f, ticketId);
    }

    [Fact]
    public async Task LocallyCreatedInteraction_IsAPersonsWork_AndLeavesTheTicketAlone()
    {
        var (f, ticketId, _) = await IngestAsync();
        await f.Interactions.AddAsync(TicketInteraction.CreateLocal(ticketId, WellKnownChannels.Phone, Phone, f.Clock.GetUtcNow().UtcDateTime));
        f.Clock.Advance(Threshold + TimeSpan.FromMinutes(1));

        await AssertLeftAloneAsync(f, ticketId);
    }

    private static async Task AssertLeftAloneAsync(GenesysServiceFixture f, long ticketId)
    {
        var auditBefore = f.Audit.Entries.Count;
        var historyBefore = f.StatusHistory.Added.Count;

        Assert.Equal(AiOnlyAutoCloseOutcome.NotEligible, await f.AiOnlyClose.TryCloseAsync(ticketId));

        Assert.NotEqual(TicketStatus.Closed, f.Tickets.All.Single(t => t.TicketId == ticketId).TicketStatus);
        Assert.Empty(f.Resolutions.Added);
        Assert.Equal(auditBefore, f.Audit.Entries.Count);
        Assert.Equal(historyBefore, f.StatusHistory.Added.Count);
    }

    // ---- already closed / not open / idempotent ----

    [Fact]
    public async Task AlreadyClosedTicket_IsNeverTouchedAgain_AndTheReasonIsNotOverwritten()
    {
        var (f, ticketId, conv) = await IngestAsync();
        await f.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto(conv, ClosedByAi: true));
        Assert.Equal(TicketStatus.Closed, f.Tickets.All.Single().TicketStatus);
        f.Clock.Advance(Threshold + TimeSpan.FromHours(5));
        var resolutions = f.Resolutions.Added.Count;
        var audit = f.Audit.Entries.Count;

        Assert.Empty(await f.AiOnlyClose.ListCandidatesAsync(0));
        Assert.Equal(AiOnlyAutoCloseOutcome.NotEligible, await f.AiOnlyClose.TryCloseAsync(ticketId));

        Assert.Equal(resolutions, f.Resolutions.Added.Count);
        Assert.Equal(audit, f.Audit.Entries.Count);
        Assert.Equal("Closed by AI", f.Resolutions.Added.Single().ResolutionNote);
    }

    [Fact]
    public async Task RunningTwice_ClosesOnce_AndWritesNothingTheSecondTime()
    {
        var (f, _, _) = await IngestAsync();
        f.Clock.Advance(Threshold + TimeSpan.FromMinutes(1));

        Assert.Equal([AiOnlyAutoCloseOutcome.Closed], await RunJobAsync(f));
        var resolutions = f.Resolutions.Added.Count;
        var audit = f.Audit.Entries.Count;
        var history = f.StatusHistory.Added.Count;

        Assert.Empty(await RunJobAsync(f));
        Assert.Equal(resolutions, f.Resolutions.Added.Count);
        Assert.Equal(audit, f.Audit.Entries.Count);
        Assert.Equal(history, f.StatusHistory.Added.Count);
        Assert.Single(f.Audit.Entries, e => e.Action == TicketClosureAuditActions.ClosedForAiOnlyTimeout);
    }

    [Fact]
    public async Task DirectRetryOfTheSameTicket_IsANoOp()
    {
        var (f, ticketId, _) = await IngestAsync();
        f.Clock.Advance(Threshold + TimeSpan.FromMinutes(1));

        Assert.Equal(AiOnlyAutoCloseOutcome.Closed, await f.AiOnlyClose.TryCloseAsync(ticketId));
        Assert.Equal(AiOnlyAutoCloseOutcome.NotEligible, await f.AiOnlyClose.TryCloseAsync(ticketId));
        Assert.Single(f.Audit.Entries, e => e.Action == TicketClosureAuditActions.ClosedForAiOnlyTimeout);
    }

    // ---- existing backlog, small batches ----

    [Fact]
    public async Task ExistingBacklog_IsPickedUp_InSmallKeysetBatches()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        for (var n = 1; n <= 5; n++)
        {
            await f.Ingestion.IngestAsync(
                ServiceAccount, new GenesysInquiryDto($"conv-{n}", GenesysChannel.WebsiteChat, CustomerPhone: Phone, DepartmentId: department.DepartmentId));
        }

        f.AiOnlyOptions.BatchSize = 2;
        f.Clock.Advance(Threshold + TimeSpan.FromMinutes(1));

        var first = await f.AiOnlyClose.ListCandidatesAsync(0);
        Assert.Equal(2, first.Count);
        var second = await f.AiOnlyClose.ListCandidatesAsync(first[^1]);
        Assert.Equal(2, second.Count);
        var third = await f.AiOnlyClose.ListCandidatesAsync(second[^1]);
        Assert.Single(third);
        Assert.Empty(await f.AiOnlyClose.ListCandidatesAsync(third[^1]));

        foreach (var id in first.Concat(second).Concat(third))
        {
            Assert.Equal(AiOnlyAutoCloseOutcome.Closed, await f.AiOnlyClose.TryCloseAsync(id));
        }

        Assert.All(f.Tickets.All, t => Assert.Equal(TicketStatus.Closed, t.TicketStatus));
    }

    [Fact]
    public async Task CustomThreshold_IsHonoured()
    {
        var (f, ticketId, _) = await IngestAsync();
        f.AiOnlyOptions.ThresholdHours = 2;
        f.Clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));

        Assert.Equal(AiOnlyAutoCloseOutcome.Closed, await f.AiOnlyClose.TryCloseAsync(ticketId));
    }

    [Fact]
    public void Options_AreOffByDefault_AndFallBackToSafeValues()
    {
        var options = new AiOnlyAutoCloseOptions();
        Assert.False(options.Enabled);
        Assert.Equal(TimeSpan.FromHours(18), options.Threshold);

        options.ThresholdHours = 0;
        options.BatchSize = 100000;
        options.MaxBatchesPerRun = 0;
        Assert.Equal(TimeSpan.FromHours(18), options.Threshold);
        Assert.Equal(200, options.EffectiveBatchSize);
        Assert.Equal(1, options.EffectiveMaxBatches);
    }
}
