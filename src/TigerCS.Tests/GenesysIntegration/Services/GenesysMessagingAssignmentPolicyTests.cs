using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Genesys is the source of truth for who handles a MESSAGING conversation (WhatsApp, web chat, Instagram, Facebook, social DM): TigerCS never deals a provisional
/// owner through Round Robin - not at creation, handoff, human follow-up, conversation end, classification, reconciliation or backfill - and assigns DIRECTLY to
/// the person Genesys confirms. Phone and non-Genesys tickets keep their own rules (see <see cref="GenesysMessagingAssignmentPolicy"/>).
/// </summary>
public class GenesysMessagingAssignmentPolicyTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    public static TheoryData<GenesysChannel> MessagingChannels => new()
    {
        GenesysChannel.WebsiteChat, GenesysChannel.WhatsApp, GenesysChannel.Instagram, GenesysChannel.Facebook, GenesysChannel.SocialMedia
    };

    private sealed record World(GenesysServiceFixture F, Department Cs, (Guid Id, string GenesysId)[] Agents)
    {
        /// <summary>The agent Round Robin would deal next (so a provisional owner would be visible as this one).</summary>
        public (Guid Id, string GenesysId) RoundRobinNext => Agents[0];

        /// <summary>The agent Genesys confirms in these tests - never the next in the rotation.</summary>
        public (Guid Id, string GenesysId) Confirmed => Agents[2];

        public Ticket Ticket(long id) => F.Tickets.All.Single(t => t.TicketId == id);
    }

    private static World NewWorld()
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true, assignOnAnswer: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var agents = Enumerable.Range(1, 3)
            .Select(i => (GenesysId: $"g-msg-{i}", Id: f.AddAgent($"g-msg-{i}", $"Agent {i}")))
            .OrderBy(a => a.Id)
            .Select(a => (a.Id, a.GenesysId))
            .ToArray();
        return new World(f, cs, agents);
    }

    private static async Task<long> IngestAsync(World w, GenesysChannel channel, string conversationId, string? agentId = null, bool? humanFollowUp = null)
    {
        var result = await w.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conversationId, channel, CustomerPhone: "+971500000003", DepartmentId: w.Cs.DepartmentId,
                AgentId: agentId, HumanFollowUpRequired: humanFollowUp));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return result.Ticket!.TicketId;
    }

    private static Task<GenesysTicketUpdateResult> UpdateAsync(World w, long ticketId, GenesysTicketUpdateDto dto) =>
        w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, dto);

    private static Task<GenesysTicketUpdateResult> ConfirmAsync(World w, long ticketId, string conversationId, string genesysAgentId, DateTime? connectedAtUtc = null) =>
        UpdateAsync(w, ticketId, new GenesysTicketUpdateDto(
            conversationId, Routing: new GenesysRoutingUpdateDto(AgentId: genesysAgentId, ConnectedAtUtc: connectedAtUtc ?? DateTime.UtcNow)));

    private static bool CursorUntouched(World w) => w.F.Rotation.Rows.Values.All(r => r.LastAssignedEmployeeId is null);

    /// <summary>No human owner, no assignment row, no Round Robin audit/history, rotation cursor untouched.</summary>
    private static void AssertWaiting(World w, long ticketId)
    {
        Assert.Null(w.Ticket(ticketId).CurrentOwnerEmployeeId);
        Assert.DoesNotContain(w.F.TicketAssignments.Added, a => a.TicketId == ticketId);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AutoAssign" && e.EntityId == ticketId.ToString());
        Assert.True(CursorUntouched(w), "A messaging ticket waiting for Genesys must not advance the shared Round Robin rotation.");
    }

    private static void AssertDirectlyAssigned(World w, long ticketId, Guid expectedOwner)
    {
        Assert.Equal(expectedOwner, w.Ticket(ticketId).CurrentOwnerEmployeeId);
        // ONE assignment row ever: the confirmed person - no intermediate Round Robin owner was created and superseded.
        var row = Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == ticketId);
        Assert.Equal(expectedOwner, row.AssignedEmployeeId);
        Assert.True(row.IsCurrent);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AutoAssign" && e.EntityId == ticketId.ToString());
        Assert.True(CursorUntouched(w));
    }

    private static int FlagCount(World w, long ticketId) =>
        w.F.WorkflowEvents.All.Count(e => e.TicketId == ticketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);

    // ---- Creation ----------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(MessagingChannels))]
    public async Task MessagingTicketCreatedWithoutHumanAssistance_StaysWithTheAiAgent_NobodyIsDealt(GenesysChannel channel)
    {
        var w = NewWorld();

        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-ai");

        AssertWaiting(w, ticketId);
        Assert.Equal(0, FlagCount(w, ticketId));
    }

    [Theory]
    [MemberData(nameof(MessagingChannels))]
    public async Task MessagingTicketCreatedRequiringAHuman_IsQueuedInTheCallCenter_WithNoProvisionalOwner(GenesysChannel channel)
    {
        var w = NewWorld();

        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-human", humanFollowUp: true);

        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(ticketId).CurrentDepartmentId);   // department routing is exactly what it was
        Assert.Equal(w.Cs.DepartmentId, w.Ticket(ticketId).OriginatingDepartmentId);
        AssertWaiting(w, ticketId);
    }

    // ---- Handoff / follow-up / identity ------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(MessagingChannels))]
    public async Task AiRequestsAHandoff_IsNotAnAcceptance_NoRoundRobin_AndTheAiOwnershipIsKept(GenesysChannel channel)
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-handoff");
        var aiOwnerBefore = w.Ticket(ticketId).AiAgentOwnerEmployeeId;

        var handoff = await w.F.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto($"conv-{channel}-handoff", AgentAvailable: false, Mode: "HumanTakeover"));

        Assert.Equal(GenesysHandoffOutcome.HandoffRecorded, handoff.Outcome);
        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(ticketId).CurrentDepartmentId);
        Assert.Equal(aiOwnerBefore, w.Ticket(ticketId).AiAgentOwnerEmployeeId);
        AssertWaiting(w, ticketId);
    }

    [Theory]
    [MemberData(nameof(MessagingChannels))]
    public async Task HumanFollowUpFlagInAnUpdate_RoutesToTheQueue_ButAssignsNobody(GenesysChannel channel)
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-followup");

        await UpdateAsync(w, ticketId, new GenesysTicketUpdateDto($"conv-{channel}-followup", HumanFollowUpRequired: true));

        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(ticketId).CurrentDepartmentId);
        AssertWaiting(w, ticketId);
    }

    [Theory]
    [MemberData(nameof(MessagingChannels))]
    public async Task NoGenesysUpdate_TheTicketKeepsWaiting_NoTimeoutOrJobAssignsIt(GenesysChannel channel)
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-silent", humanFollowUp: true);
        w.F.Clock.Advance(TimeSpan.FromDays(3));

        // Every system-driven dealing path (intake, the reconciler, the backfill, a retry of the shared rotation) is held back by the ticket's own state.
        var ticket = w.Ticket(ticketId);
        var now = w.F.Clock.GetUtcNow().UtcDateTime;
        foreach (var trigger in new[] { AutoAssignmentTrigger.CallCenterIntake, AutoAssignmentTrigger.Reconciler, AutoAssignmentTrigger.Backfill })
        {
            var ensured = await w.F.AutoAssignment.EnsureCsAgentAsync(ticket, null, trigger, now, Guid.NewGuid());
            Assert.Equal(AutoAssignmentOutcome.AwaitingGenesysConfirmation, ensured.Outcome);
        }

        Assert.Equal(AutoAssignmentOutcome.AwaitingGenesysConfirmation, (await w.F.AutoAssignment.AssignCallCenterIntakeAsync(ticket, now, Guid.NewGuid())).Outcome);
        Assert.Equal(
            AutoAssignmentOutcome.AwaitingGenesysConfirmation,
            (await w.F.AutoAssignment.AssignDepartmentRoundRobinAsync(ticket, AutoAssignmentTrigger.Reconciler, now, Guid.NewGuid())).Outcome);
        AssertWaiting(w, ticketId);
    }

    [Theory]
    [MemberData(nameof(MessagingChannels))]
    public async Task UpdateWithoutAnEmployeeIdentity_AssignsNobody(GenesysChannel channel)
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-noid", humanFollowUp: true);

        // A queue change with no agent, and a handoff update that names nobody.
        await UpdateAsync(w, ticketId, new GenesysTicketUpdateDto(
            $"conv-{channel}-noid", Routing: new GenesysRoutingUpdateDto(QueueId: "q-1", QueueName: "Messaging queue")));
        await UpdateAsync(w, ticketId, new GenesysTicketUpdateDto($"conv-{channel}-noid", Handoff: new GenesysHandoffUpdateDto(Required: true)));

        AssertWaiting(w, ticketId);
    }

    [Theory]
    [MemberData(nameof(MessagingChannels))]
    public async Task OfferedOrAssignedEmployeeOnly_IsNotAConfirmation_NobodyIsAssigned(GenesysChannel channel)
    {
        var w = NewWorld();
        // The inquiry names an agent (offered/queued) and the handoff update says who was assigned: neither is "connected".
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-offered", w.Confirmed.GenesysId, humanFollowUp: true);
        await UpdateAsync(w, ticketId, new GenesysTicketUpdateDto(
            $"conv-{channel}-offered", Handoff: new GenesysHandoffUpdateDto(Required: true, AssignedAgentId: w.Confirmed.GenesysId)));

        AssertWaiting(w, ticketId);
    }

    // ---- Unknown / unmapped / ineligible employee --------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(MessagingChannels))]
    public async Task UnmappedEmployee_IsFlaggedForManualAssignment_NoFallbackRoundRobin(GenesysChannel channel)
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-unmapped", humanFollowUp: true);

        await ConfirmAsync(w, ticketId, $"conv-{channel}-unmapped", "g-not-in-ticketing");

        AssertWaiting(w, ticketId);
        Assert.Equal(1, FlagCount(w, ticketId));
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "AnswerAssignmentSkipped" && e.EntityId == ticketId.ToString());
    }

    [Theory]
    [MemberData(nameof(MessagingChannels))]
    public async Task IneligibleEmployee_IsFlagged_AndNoUnrelatedEmployeeBecomesTheOwner(GenesysChannel channel)
    {
        var w = NewWorld();
        var notCsAgent = w.F.AddAgent("g-msg-dept-employee", "Dept Employee", roles: [Roles.DepartmentEmployee]);
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-ineligible", humanFollowUp: true);

        await ConfirmAsync(w, ticketId, $"conv-{channel}-ineligible", "g-msg-dept-employee");

        Assert.NotEqual(notCsAgent, w.Ticket(ticketId).CurrentOwnerEmployeeId);
        AssertWaiting(w, ticketId);
        Assert.Equal(1, FlagCount(w, ticketId));
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "AnswerAssignmentSkipped" && e.AfterValue!.Contains("Issue=NotCsAgent"));
    }

    // ---- Confirmation -------------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(MessagingChannels))]
    public async Task GenesysConfirmsEmployeeB_TheTicketGoesDirectlyToB_NeverThroughEmployeeA(GenesysChannel channel)
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-b", humanFollowUp: true);
        AssertWaiting(w, ticketId);   // employee A (the next in the rotation) was never dealt a provisional turn

        var result = await ConfirmAsync(w, ticketId, $"conv-{channel}-b", w.Confirmed.GenesysId);

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        AssertDirectlyAssigned(w, ticketId, w.Confirmed.Id);
        Assert.NotEqual(w.RoundRobinNext.Id, w.Ticket(ticketId).CurrentOwnerEmployeeId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(ticketId).CurrentDepartmentId);
    }

    [Theory]
    [MemberData(nameof(MessagingChannels))]
    public async Task DelayedConfirmation_AfterAHandoffAndALongWait_AssignsDirectlyOnlyThen(GenesysChannel channel)
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-late");
        await w.F.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto($"conv-{channel}-late", AgentAvailable: false, Mode: "HumanTakeover"));
        w.F.Clock.Advance(TimeSpan.FromHours(6));
        AssertWaiting(w, ticketId);

        await ConfirmAsync(w, ticketId, $"conv-{channel}-late", w.Confirmed.GenesysId);

        AssertDirectlyAssigned(w, ticketId, w.Confirmed.Id);
    }

    [Theory]
    [MemberData(nameof(MessagingChannels))]
    public async Task DuplicateConfirmation_CreatesNoSecondAssignment(GenesysChannel channel)
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-dup", humanFollowUp: true);

        await ConfirmAsync(w, ticketId, $"conv-{channel}-dup", w.Confirmed.GenesysId);
        await ConfirmAsync(w, ticketId, $"conv-{channel}-dup", w.Confirmed.GenesysId);

        AssertDirectlyAssigned(w, ticketId, w.Confirmed.Id);
        Assert.Single(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == ticketId.ToString());
    }

    [Theory]
    [MemberData(nameof(MessagingChannels))]
    public async Task OutOfOrderOlderConfirmationOfAnotherAgent_NeverOverwritesTheNewerAssignment(GenesysChannel channel)
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-order", humanFollowUp: true);
        var newer = DateTime.UtcNow.AddMinutes(-1);

        await ConfirmAsync(w, ticketId, $"conv-{channel}-order", w.Confirmed.GenesysId, connectedAtUtc: newer);
        await ConfirmAsync(w, ticketId, $"conv-{channel}-order", w.Agents[1].GenesysId, connectedAtUtc: newer.AddMinutes(-10));

        Assert.Equal(w.Confirmed.Id, w.Ticket(ticketId).CurrentOwnerEmployeeId);
        Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == ticketId && a.IsCurrent);
    }

    [Theory]
    [MemberData(nameof(MessagingChannels))]
    public async Task AuthorisedManualRetry_StillAssignsAWaitingMessagingTicket(GenesysChannel channel)
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-retry", humanFollowUp: true);

        // The Retry action of an authorised person is an explicit decision, not the system acting on its own.
        var retried = await w.F.AutoAssignment.EnsureCsAgentAsync(
            w.Ticket(ticketId), null, AutoAssignmentTrigger.ManualRetry, w.F.Clock.GetUtcNow().UtcDateTime, Guid.NewGuid());

        Assert.Equal(AutoAssignmentOutcome.AssignedRoundRobin, retried.Outcome);
        Assert.NotNull(w.Ticket(ticketId).CurrentOwnerEmployeeId);
    }

    // ---- Conversation end / AI resolution -----------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(MessagingChannels))]
    public async Task ConversationEndsWithoutEmployeeIdentification_NoRoundRobin(GenesysChannel channel)
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-ended");
        var sentAt = DateTime.UtcNow.AddMinutes(-5);

        await UpdateAsync(w, ticketId, new GenesysTicketUpdateDto(
            $"conv-{channel}-ended",
            Ended: new GenesysConversationEndUpdateDto(
                EndedAtUtc: DateTime.UtcNow.AddMinutes(-1), EndReason: "CustomerDisconnected",
                Transcript: [new GenesysTranscriptMessageDto("Customer", sentAt, "Hello"), new GenesysTranscriptMessageDto("VirtualAgent", sentAt.AddSeconds(5), "How can I help?")])));

        AssertWaiting(w, ticketId);
    }

    [Theory]
    [MemberData(nameof(MessagingChannels))]
    public async Task AnUnmappedHumanTranscriptSender_IsNoEvidence_NobodyIsDealt(GenesysChannel channel)
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-sender", humanFollowUp: true);
        var sentAt = DateTime.UtcNow.AddMinutes(-2);

        await UpdateAsync(w, ticketId, new GenesysTicketUpdateDto(
            $"conv-{channel}-sender",
            Transcript: [new GenesysTranscriptMessageDto("HumanAgent", sentAt, "Hi, this is support", SenderId: "g-unknown-sender")]));

        AssertWaiting(w, ticketId);
    }

    [Theory]
    [MemberData(nameof(MessagingChannels))]
    public async Task AMappedHumanTranscriptSender_IsTheConfirmedHandler_AssignedDirectly(GenesysChannel channel)
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-writer", humanFollowUp: true);
        var sentAt = DateTime.UtcNow.AddMinutes(-2);

        await UpdateAsync(w, ticketId, new GenesysTicketUpdateDto(
            $"conv-{channel}-writer",
            Transcript: [new GenesysTranscriptMessageDto("HumanAgent", sentAt, "Hi, this is support", SenderId: w.Confirmed.GenesysId)]));

        Assert.Equal(w.Confirmed.Id, w.Ticket(ticketId).CurrentOwnerEmployeeId);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AutoAssign" && e.EntityId == ticketId.ToString());
        Assert.True(CursorUntouched(w));
    }

    [Theory]
    [MemberData(nameof(MessagingChannels))]
    public async Task AiResolvesTheConversation_KeepsTheExistingAiClosure_AndAssignsNobody(GenesysChannel channel)
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, channel, $"conv-{channel}-resolved");

        var result = await UpdateAsync(w, ticketId, new GenesysTicketUpdateDto($"conv-{channel}-resolved", ClosedByAi: true));

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, result.Outcome);
        Assert.True(result.ClosedByAi);
        Assert.Equal(TicketStatus.Closed, w.Ticket(ticketId).TicketStatus);
        AssertWaiting(w, ticketId);
    }

    // ---- Scope: the policy never reaches voice or non-Genesys tickets ------------------------------------------------------------------------------

    [Theory]
    [InlineData(WellKnownChannels.WhatsApp, true)]
    [InlineData(WellKnownChannels.LiveChat, true)]
    [InlineData(WellKnownChannels.Instagram, true)]
    [InlineData(WellKnownChannels.Facebook, true)]
    [InlineData(WellKnownChannels.SocialMediaDirectMessage, true)]
    [InlineData(WellKnownChannels.WhatsAppOrLiveChat, true)]
    [InlineData(WellKnownChannels.Phone, false)]
    [InlineData(WellKnownChannels.FaceToFaceKiosk, false)]
    [InlineData(WellKnownChannels.Website, false)]
    [InlineData(WellKnownChannels.MobileApp, false)]
    [InlineData(WellKnownChannels.AppOrWebsite, false)]
    public void OnlyGenesysMessagingChannelsAreInScope_NeverPhoneOrOtherChannels(byte channelId, bool expected) =>
        Assert.Equal(expected, GenesysMessagingAssignmentPolicy.IsMessagingChannel(channelId));

    [Fact]
    public async Task APhoneTicket_IsNeverHeldByTheMessagingPolicy_AndKeepsItsOwnRoundRobinFlow()
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, GenesysChannel.Phone, "conv-phone-scope");

        Assert.False(await w.F.AutoAssignment.IsAwaitingGenesysMessagingAsync(w.Ticket(ticketId)));
        // The voice reconciler's own dealing (PR #307) still goes through the same engine, untouched by the messaging guard.
        var dealt = await w.F.AutoAssignment.EnsureCsAgentAsync(
            w.Ticket(ticketId), null, AutoAssignmentTrigger.CallCenterIntake, w.F.Clock.GetUtcNow().UtcDateTime, Guid.NewGuid());
        Assert.Equal(AutoAssignmentOutcome.AssignedRoundRobin, dealt.Outcome);
        Assert.Equal(w.RoundRobinNext.Id, w.Ticket(ticketId).CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task APersonsOwnership_IsNeverTouched_ByTheMessagingGuard()
    {
        var w = NewWorld();
        var ticketId = await IngestAsync(w, GenesysChannel.WhatsApp, "conv-owned-scope", humanFollowUp: true);
        await ConfirmAsync(w, ticketId, "conv-owned-scope", w.Confirmed.GenesysId);

        Assert.False(await w.F.AutoAssignment.IsAwaitingGenesysMessagingAsync(w.Ticket(ticketId)));
    }
}
