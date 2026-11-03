using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Pending human work, on any channel.
///
/// <para>
/// The requirement these cover is deliberately <b>not</b> "callbacks". Any
/// Genesys interaction that needs a human agent but cannot immediately reach
/// one must stay visible as pending work — a phone call, a website chat a bot
/// could not finish, a WhatsApp thread, a social-media message. A callback is
/// one continuation mode among several, and these prove that no part of the
/// design assumes the phone case.
/// </para>
///
/// <para>
/// These run the REAL ingestion, handoff and work-list services over fakes
/// that mirror the table's filtered unique indexes, so an idempotency test
/// cannot pass here while the real database would reject the same write.
/// </para>
/// </summary>
public class AgentHandoffTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private static readonly string[] AgentRoles = [Roles.CsAgent];

    /// <summary>
    /// For tests whose subject is the SHAPE of the work list (ordering, columns, channels), not who may see it: a CS Supervisor sees all
    /// work. A CS Agent sees only work on tickets assigned to them (or held by a department employee they are responsible for) — that is covered by
    /// <c>CsAgentTicketVisibilityTests</c>.
    /// </summary>
    private static readonly string[] SupervisorRoles = [Roles.CsSupervisor];

    private static GenesysInquiryDto Inquiry(
        string conversationId, GenesysChannel channel, int departmentId, string? customerName = null) =>
        new(conversationId, channel,
            CustomerPhone: "+971500000001", CustomerName: customerName, DepartmentId: departmentId);

    /// <summary>A CS Agent acts on a ticket only when it is assigned to them (or a department employee holds it and they are responsible for it).</summary>
    private static Guid AssignedAgent(GenesysServiceFixture f, long ticketId)
    {
        var agent = Guid.NewGuid();
        f.Tickets.All.Single(t => t.TicketId == ticketId).AssignTo(agent);
        return agent;
    }

    /// <summary>An ingested inquiry on any channel — the state every handoff starts from.</summary>
    private static async Task<(GenesysServiceFixture Fixture, long TicketId, int DepartmentId)> IngestAsync(
        string conversationId, GenesysChannel channel, GenesysServiceFixture? existing = null,
        string? customerName = null)
    {
        var f = existing ?? new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service " + channel, channel.ToString()[..3].ToUpperInvariant());

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry(conversationId, channel, department.DepartmentId, customerName: customerName));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return (f, result.Ticket!.TicketId, department.DepartmentId);
    }

    // ---- 1 & 2. Human required, with and without an agent available ----

    [Fact]
    public async Task HumanRequired_WithAnAgentAvailable_HandsOverOnTheSameTicket_AndCreatesNoSecondOne()
    {
        var (f, ticketId, _) = await IngestAsync("conv-live", GenesysChannel.WebsiteChat);

        // Genesys performs a live transfer and names the agent.
        var result = await f.AgentHandoff.RequestAsync(
            ServiceAccount,
            new GenesysHandoffRequestDto(
                "conv-live", AgentAvailable: true, Mode: "HumanTakeover", AgentId: "ga-7", AgentName: "Layla"));

        Assert.Equal(GenesysHandoffOutcome.HandoffRecorded, result.Outcome);

        // The load-bearing assertion: still ONE ticket.
        Assert.Equal(ticketId, result.TicketId);
        Assert.Single(f.Tickets.All);

        // The work is Assigned from the start — it never appears as waiting.
        var handoff = Assert.Single(f.Handoffs.All);
        Assert.Equal(AgentHandoffStatus.Assigned, handoff.Status);
        Assert.Equal("ga-7", handoff.GenesysAgentId);
        Assert.NotNull(handoff.AssignedAtUtc);

        // And the interaction learned who is on it.
        Assert.Equal("ga-7", Assert.Single(f.Interactions.All).GenesysAgentId);
    }

    [Fact]
    public async Task HumanRequired_WithNoAgentAvailable_CreatesExactlyOnePendingWorkItem()
    {
        var (f, ticketId, departmentId) = await IngestAsync("conv-wait", GenesysChannel.WebsiteChat);

        var result = await f.AgentHandoff.RequestAsync(
            ServiceAccount,
            new GenesysHandoffRequestDto("conv-wait", AgentAvailable: false, Reason: "Bot could not answer the NOC question"));

        Assert.Equal(GenesysHandoffOutcome.HandoffRecorded, result.Outcome);

        var handoff = Assert.Single(f.Handoffs.All);
        Assert.Equal(AgentHandoffStatus.WaitingForAgent, handoff.Status);
        Assert.Null(handoff.AssignedEmployeeId);
        Assert.Null(handoff.GenesysAgentId);
        Assert.Null(handoff.AssignedAtUtc);
        Assert.True(handoff.IsOpen);
        Assert.True(handoff.RequiresHumanAgent);

        // Attached to the existing ticket, in its department, on its channel.
        Assert.Equal(ticketId, handoff.TicketId);
        Assert.Equal(departmentId, handoff.DepartmentId);
        Assert.Equal("Bot could not answer the NOC question", handoff.RequestReason);
        Assert.Single(f.Tickets.All);
    }

    // ---- 3. Idempotency ----

    [Fact]
    public async Task DuplicateHandoffEvent_ReturnsTheSameWorkItem_AndCreatesNoSecondOne()
    {
        var (f, _, _) = await IngestAsync("conv-retry", GenesysChannel.WhatsApp);

        var first = await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto("conv-retry", Mode: "ReplyInChannel"));
        var second = await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto("conv-retry", Mode: "ReplyInChannel"));
        var third = await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto("conv-retry", Mode: "ReplyInChannel"));

        Assert.Equal(GenesysHandoffOutcome.HandoffRecorded, first.Outcome);
        Assert.Equal(GenesysHandoffOutcome.AlreadyRequested, second.Outcome);
        Assert.Equal(GenesysHandoffOutcome.AlreadyRequested, third.Outcome);

        Assert.Equal(first.TicketAgentHandoffId, second.TicketAgentHandoffId);
        Assert.Equal(first.TicketAgentHandoffId, third.TicketAgentHandoffId);
        Assert.Single(f.Handoffs.All);
    }

    [Fact]
    public async Task AGenesysWorkItemId_IsTheStrongerIdempotencyKey_WhenItSuppliesOne()
    {
        var (f, _, _) = await IngestAsync("conv-wi", GenesysChannel.SocialMedia);

        var first = await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto("conv-wi", WorkItemId: "wi-42"));
        var replay = await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto("conv-wi", WorkItemId: "wi-42"));

        Assert.Equal(GenesysHandoffOutcome.AlreadyRequested, replay.Outcome);
        Assert.Equal(first.TicketAgentHandoffId, replay.TicketAgentHandoffId);
        Assert.Equal("wi-42", Assert.Single(f.Handoffs.All).ExternalWorkItemId);
    }

    [Fact]
    public async Task AssignmentUpdate_AffectsTheSameWorkItem_AndIsIdempotent()
    {
        var (f, _, _) = await IngestAsync("conv-assign", GenesysChannel.WebsiteChat);
        await f.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto("conv-assign"));

        var assigned = await f.AgentHandoff.UpdateAssignmentAsync(
            ServiceAccount, new GenesysHandoffAssignmentDto("conv-assign", AgentId: "ga-9", AgentName: "Noor"));
        var replay = await f.AgentHandoff.UpdateAssignmentAsync(
            ServiceAccount, new GenesysHandoffAssignmentDto("conv-assign", AgentId: "ga-9", AgentName: "Noor"));

        Assert.Equal(GenesysHandoffOutcome.AssignmentRecorded, assigned.Outcome);
        Assert.Equal(GenesysHandoffOutcome.AssignmentRecorded, replay.Outcome);
        Assert.Equal(assigned.TicketAgentHandoffId, replay.TicketAgentHandoffId);

        // One work item, updated — never a duplicate.
        var handoff = Assert.Single(f.Handoffs.All);
        Assert.Equal(AgentHandoffStatus.Assigned, handoff.Status);
        Assert.Equal("ga-9", handoff.GenesysAgentId);

        // TigerCS does not invent an employee mapping for a Genesys agent id.
        Assert.Null(handoff.AssignedEmployeeId);
    }

    // ---- 4, 5, 6. The follow-up mode is per interaction, never per channel ----

    [Fact]
    public async Task PhoneFollowUp_CanBeRepresentedAsCallback()
    {
        var (f, _, _) = await IngestAsync("conv-phone", GenesysChannel.Phone);

        await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto("conv-phone", Mode: "Callback"));

        Assert.Equal(AgentHandoffMode.Callback, Assert.Single(f.Handoffs.All).Mode);
    }

    [Fact]
    public async Task WebsiteChatFollowUp_IsNotForcedToCallback()
    {
        var (f, _, _) = await IngestAsync("conv-chat", GenesysChannel.WebsiteChat);

        await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto("conv-chat", Mode: "ContinueChat"));

        var handoff = Assert.Single(f.Handoffs.All);
        Assert.Equal(AgentHandoffMode.ContinueChat, handoff.Mode);
        Assert.NotEqual(AgentHandoffMode.Callback, handoff.Mode);
    }

    [Theory]
    [InlineData(GenesysChannel.WhatsApp, "conv-wa")]
    [InlineData(GenesysChannel.SocialMedia, "conv-social")]
    public async Task WhatsAppAndSocialFollowUp_CanRemainReplyInChannel(GenesysChannel channel, string conversationId)
    {
        var (f, _, _) = await IngestAsync(conversationId, channel);

        await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto(conversationId, Mode: "ReplyInChannel"));

        Assert.Equal(AgentHandoffMode.ReplyInChannel, Assert.Single(f.Handoffs.All).Mode);
    }

    [Fact]
    public async Task AnUnstatedMode_StaysNull_AndIsNeverGuessedFromTheChannel()
    {
        // A phone inquiry with no mode supplied. The tempting inference —
        // "phone means callback" — is exactly the phone-shaped assumption
        // this design rejects: TigerCS records what it is told, and Genesys
        // has not confirmed how each channel continues.
        var (f, _, _) = await IngestAsync("conv-nomode", GenesysChannel.Phone);

        await f.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto("conv-nomode"));

        Assert.Null(Assert.Single(f.Handoffs.All).Mode);
    }

    [Fact]
    public async Task AnUnrecognizedMode_IsRefused_RatherThanStoredAsGenesysVocabulary()
    {
        var (f, _, _) = await IngestAsync("conv-badmode", GenesysChannel.Phone);

        var result = await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto("conv-badmode", Mode: "SCHEDULED_CALLBACK_V2"));

        Assert.Equal(GenesysHandoffOutcome.InvalidMode, result.Outcome);
        Assert.Empty(f.Handoffs.All);
    }

    // ---- 7. Unclassified tickets ----

    [Fact]
    public async Task PendingHumanWork_MayExistWhileTheTicketIsStillUnclassified()
    {
        var (f, ticketId, _) = await IngestAsync("conv-unclassified", GenesysChannel.WebsiteChat, customerName: "Ahmed Ali");

        var ticket = Assert.Single(f.Tickets.All);
        Assert.False(ticket.IsClassified);
        Assert.Equal((byte)PriorityLevel.Medium, ticket.PriorityId);

        var result = await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto("conv-unclassified"));
        Assert.Equal(GenesysHandoffOutcome.HandoffRecorded, result.Outcome);

        // And it shows up on the work list unclassified — often the agent
        // classifies it BECAUSE they picked it up from there.
        var list = await f.PendingWork.ListAsync(Guid.NewGuid(), SupervisorRoles, new AgentHandoffListRequestDto());
        var row = Assert.Single(list.Items);
        Assert.Equal(ticketId, row.TicketId);
        Assert.False(row.TicketIsClassified);
        Assert.Equal("Ahmed Ali", row.CustomerName);
    }

    // ---- 8. Completing human work never closes the ticket ----

    [Fact]
    public async Task CompletingTheHumanWork_DoesNotCloseOrOtherwiseTouchTheTicket()
    {
        var (f, ticketId, _) = await IngestAsync("conv-complete", GenesysChannel.WhatsApp);
        var requested = await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto("conv-complete", Mode: "ReplyInChannel"));

        var ticket = Assert.Single(f.Tickets.All);
        var statusBefore = ticket.TicketStatus;
        var slaBefore = ticket.SlaState;

        var agent = AssignedAgent(f, ticketId);
        var completed = await f.PendingWork.CompleteAsync(
            agent, AgentRoles, requested.TicketAgentHandoffId!.Value,
            new CompleteAgentHandoffRequestDto("Replied in the WhatsApp thread."));

        Assert.Equal(AgentHandoffOutcome.Success, completed.Outcome);
        Assert.Equal(AgentHandoffStatus.Completed, Assert.Single(f.Handoffs.All).Status);

        // The two lifecycles are separate. The customer's NOC workflow
        // continues for days after the conversation was answered.
        Assert.Equal(statusBefore, ticket.TicketStatus);
        Assert.Equal(slaBefore, ticket.SlaState);
        Assert.Null(ticket.ResolutionOutcome);
        Assert.Equal(ticketId, ticket.TicketId);
    }

    // ---- 9. Customer disconnect ----

    [Fact]
    public async Task CustomerDisconnectingWhileWaiting_EndsTheConversation_ButLeavesTheWorkActionable()
    {
        var (f, _, _) = await IngestAsync("conv-drop", GenesysChannel.WebsiteChat);
        await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto("conv-drop", Mode: "ContinueChat"));

        // The customer closes the browser while waiting for a human.
        var ended = await f.ConversationEnd.EndAsync(
            ServiceAccount,
            new GenesysConversationEndDto(
                "conv-drop", DateTime.UtcNow, "CustomerDisconnect", null, null,
                [new GenesysTranscriptMessageDto("Customer", DateTime.UtcNow, "Anyone there?")]));
        Assert.Equal(GenesysConversationEndOutcome.Ended, ended.Outcome);

        // The interaction ended, the transcript was kept — and the business
        // case did not go away with the live session.
        var interaction = Assert.Single(f.Interactions.All);
        Assert.True(interaction.IsEnded);

        var handoff = Assert.Single(f.Handoffs.All);
        Assert.True(handoff.IsOpen);
        Assert.Equal(AgentHandoffStatus.WaitingForAgent, handoff.Status);

        var list = await f.PendingWork.ListAsync(Guid.NewGuid(), SupervisorRoles, new AgentHandoffListRequestDto());
        var row = Assert.Single(list.Items);
        Assert.True(row.InteractionEnded);
        Assert.Equal("WaitingForAgent", row.Status);
    }

    // ---- 10, 11, 12. The agent work list, its context, and its actions ----

    [Fact]
    public async Task TheWorkList_ShowsWaitingItemsAcrossChannels_LongestWaitFirst()
    {
        var f = new GenesysServiceFixture();
        await IngestAsync("conv-a", GenesysChannel.Phone, f);
        await IngestAsync("conv-b", GenesysChannel.WebsiteChat, f);
        await IngestAsync("conv-c", GenesysChannel.WhatsApp, f);

        await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto("conv-a", Mode: "Callback", RequestedAtUtc: DateTime.UtcNow.AddHours(-3)));
        await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto("conv-b", Mode: "ContinueChat", RequestedAtUtc: DateTime.UtcNow.AddHours(-1)));
        await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto("conv-c", Mode: "ReplyInChannel", RequestedAtUtc: DateTime.UtcNow.AddHours(-2)));

        var list = await f.PendingWork.ListAsync(Guid.NewGuid(), SupervisorRoles, new AgentHandoffListRequestDto());

        Assert.Equal(3, list.TotalCount);
        // The customer waiting longest is surfaced first.
        Assert.Equal(
            new[] { "Callback", "ReplyInChannel", "ContinueChat" },
            list.Items.Select(i => i.Mode ?? "(unstated)").ToArray());
        // And it is emphatically not a callback list.
        Assert.Contains(list.Items, i => i.Mode != "Callback");
    }

    [Fact]
    public async Task OpeningTheWorkItemsTicket_ShowsTheBotTranscriptAndWhyAHumanWasNeeded()
    {
        var (f, ticketId, _) = await IngestAsync("conv-context", GenesysChannel.WebsiteChat, customerName: "Ahmed Ali");

        await f.AgentHandoff.RequestAsync(
            ServiceAccount,
            new GenesysHandoffRequestDto(
                "conv-context", Mode: "ContinueChat", Reason: "Customer asked about an NOC for resale"));

        // The bot conversation that led to the handoff.
        await f.ConversationEnd.EndAsync(
            ServiceAccount,
            new GenesysConversationEndDto(
                "conv-context", DateTime.UtcNow, "CustomerDisconnect", null, null,
                [
                    new GenesysTranscriptMessageDto("Customer", DateTime.UtcNow.AddMinutes(-3), "I want to sell my apartment."),
                    new GenesysTranscriptMessageDto("VirtualAgent", DateTime.UtcNow.AddMinutes(-2), "Are you asking about an NOC for resale?"),
                    new GenesysTranscriptMessageDto("Customer", DateTime.UtcNow.AddMinutes(-1), "Yes.")
                ]));

        var history = await f.InteractionQuery.GetForTicketAsync(AssignedAgent(f, ticketId), AgentRoles, ticketId);
        Assert.Equal(TicketQueryOutcome.Success, history.Outcome);

        var interaction = Assert.Single(history.Response!.Interactions);
        Assert.Equal(3, interaction.Messages.Count);

        // Which participant said what is preserved — an agent taking over
        // must be able to tell the bot's lines from a human's.
        Assert.Equal("VirtualAgent", interaction.Messages[1].Sender);
        Assert.Equal("Ahmed Ali", interaction.CustomerName);

        // And the handoff context sits right beside the transcript.
        Assert.Equal("Customer asked about an NOC for resale", interaction.Handoff!.RequestReason);
        Assert.Equal("ContinueChat", interaction.Handoff.Mode);
        Assert.Equal("WaitingForAgent", interaction.Handoff.Status);
    }

    [Fact]
    public async Task AnAgentStartsAndFinishesTheWork_OnOneItemThroughout()
    {
        var (f, _, _) = await IngestAsync("conv-work", GenesysChannel.WebsiteChat);
        var requested = await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto("conv-work"));
        var handoffId = requested.TicketAgentHandoffId!.Value;
        var agent = Guid.NewGuid();

        // Accepting makes the agent the ticket's owner, so they must belong to
        // its current department — the approved rule.
        f.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(
                agent, Assert.Single(f.Handoffs.All).DepartmentId, isPrimary: true, DateTime.UtcNow, null));

        var started = await f.PendingWork.AcceptAsync(agent, AgentRoles, handoffId);
        Assert.Equal(AgentHandoffOutcome.Success, started.Outcome);
        Assert.Equal("InProgress", started.Handoff!.Status);
        // Accepting also takes it — no separate claim step.
        Assert.Equal(agent, started.Handoff.AssignedEmployeeId);

        // Idempotent: a double-clicked button does not move the start time.
        var startedAt = Assert.Single(f.Handoffs.All).StartedAtUtc;
        var again = await f.PendingWork.AcceptAsync(agent, AgentRoles, handoffId);
        Assert.Equal(AgentHandoffOutcome.Success, again.Outcome);
        Assert.Equal(startedAt, Assert.Single(f.Handoffs.All).StartedAtUtc);

        var completed = await f.PendingWork.CompleteAsync(
            agent, AgentRoles, handoffId, new CompleteAgentHandoffRequestDto("Answered in chat."));
        Assert.Equal(AgentHandoffOutcome.Success, completed.Outcome);

        // Still one work item, and it drops off the default list.
        Assert.Single(f.Handoffs.All);
        Assert.Empty((await f.PendingWork.ListAsync(agent, AgentRoles, new AgentHandoffListRequestDto())).Items);
    }

    [Fact]
    public async Task CompletedWork_AcceptsNoFurtherChanges()
    {
        var (f, ticketId, _) = await IngestAsync("conv-done", GenesysChannel.Phone);
        var requested = await f.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto("conv-done"));
        var handoffId = requested.TicketAgentHandoffId!.Value;
        var agent = AssignedAgent(f, ticketId);

        await f.PendingWork.CompleteAsync(agent, AgentRoles, handoffId, new CompleteAgentHandoffRequestDto(null));

        Assert.Equal(
            AgentHandoffOutcome.AlreadyResolved,
            (await f.PendingWork.CompleteAsync(agent, AgentRoles, handoffId, new CompleteAgentHandoffRequestDto(null))).Outcome);
        Assert.Equal(
            AgentHandoffOutcome.AlreadyResolved,
            (await f.PendingWork.CancelAsync(agent, AgentRoles, handoffId, new CancelAgentHandoffRequestDto("Too late"))).Outcome);
    }

    [Fact]
    public async Task CancellingRequiresAReason_BecauseCustomerWorkIsNeverDroppedSilently()
    {
        var (f, ticketId, _) = await IngestAsync("conv-cancel", GenesysChannel.SocialMedia);
        var requested = await f.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto("conv-cancel"));
        var handoffId = requested.TicketAgentHandoffId!.Value;
        var agent = AssignedAgent(f, ticketId);

        var noReason = await f.PendingWork.CancelAsync(
            agent, AgentRoles, handoffId, new CancelAgentHandoffRequestDto("   "));
        Assert.Equal(AgentHandoffOutcome.ReasonRequired, noReason.Outcome);
        Assert.True(Assert.Single(f.Handoffs.All).IsOpen);

        var cancelled = await f.PendingWork.CancelAsync(
            agent, AgentRoles, handoffId, new CancelAgentHandoffRequestDto("Customer answered themselves in the thread."));
        Assert.Equal(AgentHandoffOutcome.Success, cancelled.Outcome);

        var handoff = Assert.Single(f.Handoffs.All);
        Assert.Equal(AgentHandoffStatus.Cancelled, handoff.Status);
        Assert.Equal("Customer answered themselves in the thread.", handoff.ResolutionNote);
        // Cancelling the work does not touch the ticket either.
        Assert.Equal(TicketStatus.Open, Assert.Single(f.Tickets.All).TicketStatus);
    }

    [Fact]
    public async Task ResolvingTheWork_FreesTheInteractionForALaterHandoff()
    {
        // The unique index is on OPEN work per interaction, not on the
        // interaction: a customer who comes back needing a human again is a
        // new piece of work on the same ticket, never a new ticket.
        var (f, ticketId, _) = await IngestAsync("conv-again", GenesysChannel.WhatsApp);
        var first = await f.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto("conv-again"));
        var agent = AssignedAgent(f, ticketId);
        await f.PendingWork.CompleteAsync(
            agent, AgentRoles, first.TicketAgentHandoffId!.Value, new CompleteAgentHandoffRequestDto("Answered."));

        var second = await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto("conv-again", Reason: "Customer replied with a new question"));

        Assert.Equal(GenesysHandoffOutcome.HandoffRecorded, second.Outcome);
        Assert.NotEqual(first.TicketAgentHandoffId, second.TicketAgentHandoffId);
        Assert.Equal(2, f.Handoffs.All.Count);

        // Two work items, one ticket.
        Assert.Equal(ticketId, second.TicketId);
        Assert.Single(f.Tickets.All);
    }

    // ---- Refusals at the boundary ----

    [Fact]
    public async Task AConversationThatNeverProducedATicket_HasNothingToAttachHumanWorkTo()
    {
        var f = new GenesysServiceFixture();

        var result = await f.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto("conv-never-ingested"));

        Assert.Equal(GenesysHandoffOutcome.ConversationNotFound, result.Outcome);
        Assert.Empty(f.Handoffs.All);
    }

    [Fact]
    public async Task WithTheIntegrationDisabled_NoHandoffIsProcessed()
    {
        var f = new GenesysServiceFixture(enabled: false);

        var result = await f.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto("conv-x"));

        Assert.Equal(GenesysHandoffOutcome.IntegrationDisabled, result.Outcome);
        Assert.Empty(f.Handoffs.All);
    }

    [Fact]
    public async Task AnAssignmentMustNameSomeone_AndNeedsOutstandingWorkToApplyTo()
    {
        var (f, _, _) = await IngestAsync("conv-noagent", GenesysChannel.Phone);

        Assert.Equal(
            GenesysHandoffOutcome.NoOpenHandoff,
            (await f.AgentHandoff.UpdateAssignmentAsync(
                ServiceAccount, new GenesysHandoffAssignmentDto("conv-noagent", AgentId: "ga-1"))).Outcome);

        await f.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto("conv-noagent"));

        Assert.Equal(
            GenesysHandoffOutcome.AgentRequired,
            (await f.AgentHandoff.UpdateAssignmentAsync(
                ServiceAccount, new GenesysHandoffAssignmentDto("conv-noagent", AgentId: "  "))).Outcome);
    }
}
