using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// <b>WhatsApp conversation lifecycle (PR #292).</b> A WhatsApp ticket keeps the Digital assignment behaviour (Call Center first + ONE Round Robin agent) but only when a person is
/// actually needed - never merely because a conversation is active or Genesys has not yet sent its next update. The conversation status is derived and separate from the
/// ticket status. Phone is untouched (see <see cref="VoiceOwnershipRulesTests"/>).
/// </summary>
public class WhatsAppConversationLifecycleTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    private sealed class World
    {
        public required GenesysServiceFixture F { get; init; }
        public required Department Cs { get; init; }
        public required (Guid Id, string GenesysId)[] Agents { get; init; }
        public Ticket Ticket(long id) => F.Tickets.All.Single(t => t.TicketId == id);
        public bool RotationUntouched => F.Rotation.Rows.Values.All(r => r.LastAssignedEmployeeId is null);
    }

    private static World NewWorld()
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true, assignOnAnswer: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var agents = Enumerable.Range(1, 3).Select(i => (Id: f.AddAgent($"g-wa-{i}", $"Agent {i}"), GenesysId: $"g-wa-{i}")).OrderBy(a => a.Id).ToArray();
        return new World { F = f, Cs = cs, Agents = agents };
    }

    private static async Task<(long TicketId, string ConversationId)> WhatsAppAsync(World w, string conversationId, bool humanFollowUp = false)
    {
        var r = await w.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conversationId, GenesysChannel.WhatsApp, CustomerPhone: "+971500000009", DepartmentId: w.Cs.DepartmentId,
                HumanFollowUpRequired: humanFollowUp ? true : null));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, r.Outcome);
        return (r.Ticket!.TicketId, conversationId);
    }

    private static Task<GenesysTicketUpdateResult> UpdateAsync(World w, (long TicketId, string ConversationId) t, GenesysTicketUpdateDto dto) =>
        w.F.TicketUpdate.UpdateAsync(ServiceAccount, t.TicketId, dto);

    private static async Task CloseByAiAsync(World w, (long TicketId, string ConversationId) t)
    {
        var closed = await UpdateAsync(w, t, new GenesysTicketUpdateDto(t.ConversationId, ClosedByAi: true));
        Assert.True(closed.ClosedByAi, closed.ClosedByAiNote);
        Assert.Equal(TicketStatus.Closed, w.Ticket(t.TicketId).TicketStatus);
    }

    private static async Task<string?> StatusAsync(World w, (long TicketId, string ConversationId) t) =>
        Assert.Single((await w.F.InteractionQuery.GetForTicketAsync(Guid.NewGuid(), [Roles.SystemAdministrator], t.TicketId)).Response!.Interactions).ConversationStatus;

    // ---- an active / waiting conversation is never Round-Robined --------------------------------------------------------------------

    [Fact]
    public async Task AnActiveAiWhatsAppConversation_IsInProgress_OwnedByTheAiAgent_AndNobodyIsDealt()
    {
        var w = NewWorld();
        var chat = await WhatsAppAsync(w, "wa-active");

        var t = w.Ticket(chat.TicketId);
        Assert.Null(t.CurrentOwnerEmployeeId);                                                 // not "assigned" to any person ...
        Assert.NotNull(t.AiAgentOwnerEmployeeId);                                              // ... the AI Agent owns it (shown as AI, never ordinary Unassigned)
        Assert.True(w.RotationUntouched);
        Assert.Equal(GenesysConversationStatus.InProgress, await StatusAsync(w, chat));
        Assert.Equal(TicketStatus.Open, t.TicketStatus);                                       // the conversation state is separate from the ticket status
    }

    [Fact]
    public async Task QueueChangesTranscriptsAndARingingAgent_KeepItAiOwned_NoRoundRobinNoAssignment()
    {
        var w = NewWorld();
        var chat = await WhatsAppAsync(w, "wa-noise");

        await UpdateAsync(w, chat, new GenesysTicketUpdateDto(chat.ConversationId, Routing: new GenesysRoutingUpdateDto(QueueId: "q-1", QueueName: "WhatsApp")));
        await UpdateAsync(w, chat, new GenesysTicketUpdateDto(chat.ConversationId, AgentId: w.Agents[1].GenesysId, AgentName: "Ringing"));   // assigned / ringing, not connected

        var t = w.Ticket(chat.TicketId);
        Assert.Null(t.CurrentOwnerEmployeeId);
        Assert.NotNull(t.AiAgentOwnerEmployeeId);
        Assert.True(w.RotationUntouched);
        Assert.False(w.F.InteractionFor(chat.ConversationId)!.RequiresHumanFollowUp);
        Assert.Equal(GenesysConversationStatus.InProgress, await StatusAsync(w, chat));
    }

    [Fact]
    public async Task ARepeatedOrDelayedUpdate_ForAnActiveConversation_ChangesNothing()
    {
        var w = NewWorld();
        var chat = await WhatsAppAsync(w, "wa-repeat");
        var dto = new GenesysTicketUpdateDto(chat.ConversationId, Routing: new GenesysRoutingUpdateDto(QueueId: "q-1", QueueName: "WhatsApp"));

        await UpdateAsync(w, chat, dto);
        await UpdateAsync(w, chat, dto);

        Assert.Null(w.Ticket(chat.TicketId).CurrentOwnerEmployeeId);
        Assert.True(w.RotationUntouched);
    }

    // ---- Genesys confirms a person is needed: the approved Digital workflow ----------------------------------------------------------

    [Fact]
    public async Task WhenGenesysConfirmsAPersonIsNeeded_TheDigitalWorkflowRuns_CallCenterFirstThenOneRoundRobinAgent_AndTheStatusAwaitsConfirmation()
    {
        var w = NewWorld();
        var chat = await WhatsAppAsync(w, "wa-human");

        await UpdateAsync(w, chat, new GenesysTicketUpdateDto(chat.ConversationId, HumanFollowUpRequired: true));

        var t = w.Ticket(chat.TicketId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, t.CurrentDepartmentId);
        Assert.Equal(w.Agents[0].Id, t.CurrentOwnerEmployeeId);                                // ONE agent, exactly as on main before the snapshot
        Assert.Equal(GenesysConversationStatus.AwaitingGenesysUpdate, await StatusAsync(w, chat));   // owner is provisional: no connection confirmed yet
        Assert.Equal(TicketStatus.Open, t.TicketStatus);
    }

    [Fact]
    public async Task AConfirmedConnection_AssignsTheAnsweringAgent_AndTheStatusIsAssigned()
    {
        var w = NewWorld();
        var chat = await WhatsAppAsync(w, "wa-connect", humanFollowUp: true);

        await UpdateAsync(w, chat, new GenesysTicketUpdateDto(chat.ConversationId, Routing: new GenesysRoutingUpdateDto(AgentId: w.Agents[2].GenesysId, ConnectedAtUtc: DateTime.UtcNow.AddSeconds(-5))));
        await UpdateAsync(w, chat, new GenesysTicketUpdateDto(chat.ConversationId, Routing: new GenesysRoutingUpdateDto(AgentId: w.Agents[2].GenesysId, ConnectedAtUtc: DateTime.UtcNow.AddSeconds(-5))));   // duplicate

        Assert.Equal(w.Agents[2].Id, w.Ticket(chat.TicketId).CurrentOwnerEmployeeId);
        Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == chat.TicketId && a.AssignedEmployeeId == w.Agents[2].Id);   // idempotent
        Assert.Equal(GenesysConversationStatus.Assigned, await StatusAsync(w, chat));
    }

    [Fact]
    public async Task AnEndedConversation_IsEnded_AndEndingNeverClosesTheTicket()
    {
        var w = NewWorld();
        var chat = await WhatsAppAsync(w, "wa-ended");
        await UpdateAsync(w, chat, new GenesysTicketUpdateDto(chat.ConversationId, ClosedByAi: null,
            Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow.AddSeconds(-1), "CustomerDisconnect"), HumanFollowUpRequired: true));

        Assert.Equal(GenesysConversationStatus.Ended, await StatusAsync(w, chat));
        Assert.NotEqual(TicketStatus.Closed, w.Ticket(chat.TicketId).TicketStatus);
    }

    // ---- late Genesys updates on a Closed ticket -----------------------------------------------------------------------------------

    [Fact]
    public async Task ALateGenesysUpdate_OnAClosedWhatsAppTicket_ChangesNothing_NoReopenNoOwnerChangeNoRoundRobin()
    {
        var w = NewWorld();
        var chat = await WhatsAppAsync(w, "wa-closed");
        await CloseByAiAsync(w, chat);
        var ticket = w.Ticket(chat.TicketId);
        var ownerBefore = ticket.CurrentOwnerEmployeeId;
        var rotationBefore = w.F.Rotation.Rows.Values.Select(r => r.LastAssignedEmployeeId).ToArray();

        await UpdateAsync(w, chat, new GenesysTicketUpdateDto(chat.ConversationId, HumanFollowUpRequired: true,
            Routing: new GenesysRoutingUpdateDto(AgentId: w.Agents[1].GenesysId, ConnectedAtUtc: DateTime.UtcNow.AddSeconds(-5))));
        await w.F.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto(chat.ConversationId, AgentAvailable: true, Mode: "HumanTakeover"));

        var after = w.Ticket(chat.TicketId);
        Assert.Equal(TicketStatus.Closed, after.TicketStatus);
        Assert.Equal(ownerBefore, after.CurrentOwnerEmployeeId);
        Assert.Equal(rotationBefore, w.F.Rotation.Rows.Values.Select(r => r.LastAssignedEmployeeId).ToArray());
    }

    [Fact]
    public async Task AFreshWhatsAppConversation_AfterAClosure_IsANewTicket_TheClosedOneIsNeverReused()
    {
        var w = NewWorld();
        var first = await WhatsAppAsync(w, "wa-first");
        await CloseByAiAsync(w, first);

        var second = await WhatsAppAsync(w, "wa-second");                                      // the customer writes again: a new Genesys conversation id

        Assert.NotEqual(first.TicketId, second.TicketId);
        Assert.Equal(TicketStatus.Closed, w.Ticket(first.TicketId).TicketStatus);
        Assert.Equal(TicketStatus.Open, w.Ticket(second.TicketId).TicketStatus);
    }

    [Fact]
    public async Task ReIngestingTheSameConversation_AfterClosure_ReturnsTheClosedTicket_AndReopensNothing()
    {
        var w = NewWorld();
        var chat = await WhatsAppAsync(w, "wa-same");
        await CloseByAiAsync(w, chat);

        var again = await w.F.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto("wa-same", GenesysChannel.WhatsApp, CustomerPhone: "+971500000009", DepartmentId: w.Cs.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, again.Outcome);                  // idempotent on the conversation id: no second ticket, no reopen
        Assert.Equal(TicketStatus.Closed, w.Ticket(chat.TicketId).TicketStatus);
    }

    // ---- background jobs respect an active / waiting WhatsApp conversation ----------------------------------------------------------

    private static UnassignedTicketEvidence Live(bool humanRequired = false, bool endedNoHuman = false) =>
        new(1, "TG-1", TicketStatus.Open, null, 10, 10, Now.AddHours(-1), false, HasGenesysInteraction: true, HasLiveGenesysInteraction: !endedNoHuman,
            RequiresHumanFollowUp: humanRequired, OpenHandoff: false, HumanEvidence: false, AnsweringEmployeeId: null, AnsweringGenesysUserId: null,
            PhonePickupByUnmappedAgent: false, UnmappedGenesysAgentId: null, LastActivityUtc: Now.AddMinutes(-30),
            LeftCallCenter: false, ReleasedByPerson: false, GenesysIsPhoneOnly: false);

    [Fact]
    public void TheCallCenterReconciliation_LeavesALiveAiWhatsAppConversationAlone_EvenWhenTheStaleSettingIsOn_UnlessSilentPastTheThreshold()
    {
        // default production configuration: no stale threshold, stale-AI action off -> always protected
        var protectedByDefault = UnassignedTicketClassifier.Classify(Live(), callCenterDepartmentId: 99, Now);
        Assert.Equal(UnassignedTicketAction.None, protectedByDefault.Action);

        // active within the threshold: protected even when the stale rule is enabled
        var recent = UnassignedTicketClassifier.Classify(Live(), 99, Now, staleAiAfter: TimeSpan.FromHours(6), actOnStaleAi: true);
        Assert.Equal(UnassignedTicketAction.None, recent.Action);
    }

    [Fact]
    public void TheVoiceReconciler_NeverClassifiesAWhatsAppTicket()
    {
        var c = VoiceCallClassifier.Classify(Live(endedNoHuman: true), Now, new VoiceCallReconciliationOptions());
        Assert.Equal(VoiceReconcileClass.NotApplicable, c.Class);
        Assert.Equal(VoiceReconcileAction.None, c.Action);
    }

    // ---- conversation status mapping (pure) ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false, "Ai", false, GenesysConversationStatus.InProgress)]
    [InlineData(false, false, "Ai", true, GenesysConversationStatus.AwaitingGenesysUpdate)]    // digital: handling state stays "Ai"; the flag marks awaiting
    [InlineData(false, true, "Ai", true, GenesysConversationStatus.Assigned)]
    [InlineData(true, true, "Human", true, GenesysConversationStatus.Ended)]
    [InlineData(false, false, "Human", false, GenesysConversationStatus.InProgress)]   // a recorded handler alone (e.g. an assigned / ringing agent) is not a confirmation
    [InlineData(true, false, "Ai", false, GenesysConversationStatus.Ended)]
    public void StatusMapping(bool ended, bool confirmed, string handling, bool required, string expected) =>
        Assert.Equal(expected, GenesysConversationStatus.Derive(ended, confirmed, handling, required));
}
