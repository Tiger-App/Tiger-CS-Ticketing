using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The voice no-answer reconciliation (PR #292, P0 fix): "the call ended and nobody answered" is concluded only after a configurable window measured from when the end was
/// RECEIVED, then dealt to an eligible Call Center CS Agent by the existing Round Robin (never to an agent who was merely OFFERED the call); when nobody is eligible the
/// ticket stays queued and is retried every run. An answering agent whose Genesys id was unmapped is reconciled - never Round Robin - once the mapping is corrected.
/// Pure classification first, then the executor through the real services.
/// </summary>
public class VoiceCallReconcilerTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    // ======================================================================================================================
    // 1. The classifier (pure)
    // ======================================================================================================================

    private static UnassignedTicketEvidence Ev(
        bool phoneOnly = true, TicketStatus status = TicketStatus.Open, Guid? owner = null, DateTime? endReceived = null, bool humanAnswered = false,
        bool phoneUnmapped = false, string? flaggedId = null, Guid? flaggedEmployee = null, Guid? answering = null, bool approval = false) =>
        new(1, "TG-1", status, owner, 10, 10, Now.AddHours(-1), approval, HasGenesysInteraction: true, HasLiveGenesysInteraction: endReceived is null,
            RequiresHumanFollowUp: false, OpenHandoff: false, HumanEvidence: humanAnswered, AnsweringEmployeeId: answering, AnsweringGenesysUserId: answering is null ? null : "g-1",
            PhonePickupByUnmappedAgent: phoneUnmapped, UnmappedGenesysAgentId: phoneUnmapped ? "g-x" : null, LastActivityUtc: Now.AddMinutes(-30),
            LeftCallCenter: false, ReleasedByPerson: false, GenesysIsPhoneOnly: phoneOnly, VoiceEndReceivedAtUtc: endReceived,
            FlaggedAnsweringGenesysUserId: flaggedId, FlaggedAnsweringEmployeeId: flaggedEmployee, HumanAnswered: humanAnswered || flaggedId is not null || answering is not null);

    private static VoiceReconcileClassification C(UnassignedTicketEvidence e, int windowSeconds = 300, int lookbackHours = 24) =>
        VoiceCallClassifier.Classify(e, Now, new VoiceCallReconciliationOptions { NoAnswerWindowSeconds = windowSeconds, LookbackHours = lookbackHours });

    [Fact]
    public void DefaultsAreDocumented_FiveMinuteWindow_24HourLookback_EnabledAndLive()
    {
        var o = new VoiceCallReconciliationOptions();
        Assert.True(o.Enabled);
        Assert.False(o.DryRun);
        Assert.Equal(300, o.NoAnswerWindowSeconds);
        Assert.Equal(TimeSpan.FromMinutes(5), o.NoAnswerWindow);
        Assert.Equal(TimeSpan.FromHours(24), o.Lookback);
        Assert.Equal(TimeSpan.FromMinutes(5), new VoiceCallReconciliationOptions { NoAnswerWindowSeconds = -1 }.NoAnswerWindow);   // nonsense falls back to the default
    }

    [Fact]
    public void ADigitalOrMixedTicket_IsNeverThisJobsBusiness() =>
        Assert.Equal(VoiceReconcileClass.NotApplicable, C(Ev(phoneOnly: false, endReceived: Now.AddHours(-1))).Class);

    [Theory]
    [InlineData(TicketStatus.Closed)]
    [InlineData(TicketStatus.Resolved)]
    [InlineData(TicketStatus.InProgress)]
    public void ANotOpenTicket_IsNeverTouched(TicketStatus status) =>
        Assert.Equal(new VoiceReconcileClassification(VoiceReconcileClass.NotApplicable, VoiceReconcileAction.None), C(Ev(status: status, endReceived: Now.AddHours(-1))));

    [Fact]
    public void AnOwnedTicket_IsNeverTouched() =>
        Assert.Equal(VoiceReconcileClass.NotApplicable, C(Ev(owner: Guid.NewGuid(), endReceived: Now.AddHours(-1))).Class);

    [Fact]
    public void ACallStillRinging_OrWithNoEndReceived_NeverConcludes() =>
        Assert.Equal(new VoiceReconcileClassification(VoiceReconcileClass.CallInProgress, VoiceReconcileAction.None), C(Ev(endReceived: null)));

    [Theory]
    [InlineData(0, true)]
    [InlineData(299, true)]
    [InlineData(300, false)]
    [InlineData(301, false)]
    public void TheWindow_IsMeasuredFromTheReceiptOfTheEnd_AndIsInclusiveAtTheBoundary(int secondsSinceEnd, bool stillWaiting)
    {
        var result = C(Ev(endReceived: Now.AddSeconds(-secondsSinceEnd)));

        Assert.Equal(stillWaiting ? VoiceReconcileClass.WaitingForRoutingWindow : VoiceReconcileClass.NoAnswerConcluded, result.Class);
        Assert.Equal(stillWaiting ? VoiceReconcileAction.None : VoiceReconcileAction.ConcludeNoAnswer, result.Action);
    }

    [Fact]
    public void TheWindowIsConfigurable_AndZeroConcludesImmediately()
    {
        Assert.Equal(VoiceReconcileClass.WaitingForRoutingWindow, C(Ev(endReceived: Now.AddSeconds(-100)), windowSeconds: 120).Class);
        Assert.Equal(VoiceReconcileClass.NoAnswerConcluded, C(Ev(endReceived: Now.AddSeconds(-100)), windowSeconds: 60).Class);
        Assert.Equal(VoiceReconcileClass.NoAnswerConcluded, C(Ev(endReceived: Now), windowSeconds: 0).Class);
    }

    [Fact]
    public void ACallThatEndedLongerAgoThanTheLookback_IsNeverSweptUp() =>
        Assert.Equal(VoiceReconcileClass.OutsideLookback, C(Ev(endReceived: Now.AddHours(-25))).Class);

    [Fact]
    public void ACallAPersonAnswered_IsNotANoAnswer_EvenWhenNothingAssignedThem() =>
        Assert.Equal(new VoiceReconcileClassification(VoiceReconcileClass.HumanInvolved, VoiceReconcileAction.None), C(Ev(endReceived: Now.AddHours(-1), humanAnswered: true)));

    [Fact]
    public void AFlaggedAnswer_WhoseMappingIsStillMissing_StaysQueued_NeverRoundRobin() =>
        Assert.Equal(new VoiceReconcileClassification(VoiceReconcileClass.AnsweringAgentMappingPending, VoiceReconcileAction.None),
            C(Ev(endReceived: Now.AddHours(-1), flaggedId: "g-x")));

    [Fact]
    public void AFlaggedAnswer_WhoseMappingWasCorrected_IsReconciledToThatAgent() =>
        Assert.Equal(new VoiceReconcileClassification(VoiceReconcileClass.AnsweringAgentResolvable, VoiceReconcileAction.AssignAnsweringAgent),
            C(Ev(endReceived: Now.AddHours(-1), flaggedId: "g-x", flaggedEmployee: Guid.NewGuid())));

    [Fact]
    public void AFlaggedAnswer_IsReconciledHoweverOldTheCall_ButNeverRoundRobinned() =>
        Assert.Equal(VoiceReconcileAction.AssignAnsweringAgent, C(Ev(endReceived: Now.AddDays(-30), flaggedId: "g-x", flaggedEmployee: Guid.NewGuid())).Action);

    [Fact]
    public void ARecordedHandlerWhoNeverGotTheTicket_IsGivenIt() =>
        Assert.Equal(VoiceReconcileAction.AssignAnsweringAgent, C(Ev(endReceived: Now.AddMinutes(-1), answering: Guid.NewGuid())).Action);

    [Fact]
    public void ANeedsPersonTicket_StuckOnTheAiAgent_IsRecoveredHoweverOldTheCall()
    {
        var old = Ev(endReceived: Now.AddDays(-30)) with { RequiresHumanFollowUp = true };

        Assert.Equal(new VoiceReconcileClassification(VoiceReconcileClass.NoAnswerConcluded, VoiceReconcileAction.ConcludeNoAnswer), C(old));
        Assert.Equal(VoiceReconcileClass.OutsideLookback, C(Ev(endReceived: Now.AddDays(-30))).Class);   // an AI-handled call nobody asked a person for stays untouched
    }

    [Fact]
    public void ATicketAPersonReleasedToTheQueue_IsNeverDealtAgain() =>
        Assert.Equal(new VoiceReconcileClassification(VoiceReconcileClass.ReleasedByPerson, VoiceReconcileAction.None),
            C(Ev(endReceived: Now.AddHours(-1)) with { ReleasedByPerson = true, RequiresHumanFollowUp = true }));

    [Fact]
    public void APendingApproval_IsLeftToTheApprovalProcess() =>
        Assert.Equal(VoiceReconcileClass.PendingApproval, C(Ev(endReceived: Now.AddHours(-1), approval: true)).Class);

    // ======================================================================================================================
    // 2. The executor, through the real services
    // ======================================================================================================================

    private sealed class FixtureEvidence(GenesysServiceFixture f) : ITicketUnassignedWorkRepository
    {
        public async Task<UnassignedTicketEvidence?> GetEvidenceAsync(long ticketId, CancellationToken cancellationToken = default)
        {
            var t = f.Tickets.All.SingleOrDefault(x => x.TicketId == ticketId);
            if (t is null) return null;

            var interactions = f.Interactions.All.Where(i => i.TicketId == ticketId).ToList();
            var genesys = interactions.Where(i => i.Source == InteractionContextSource.Genesys).ToList();
            var live = genesys.Where(i => i.EndedAtUtc == null && i.InactivityClosedAtUtc == null).ToList();
            // Mirrors TicketUnassignedWorkRepository: only CONFIRMED connection counts as an answer (pickup, routing-connected, transcript), never a top-level agentId.
            var handlers = f.Handlers.All.Where(h => h.TicketId == ticketId
                && h.Source is InteractionHandlerSource.Inquiry or InteractionHandlerSource.Routing or InteractionHandlerSource.Transcript or InteractionHandlerSource.Legacy).ToList();
            var events = f.WorkflowEvents.All.Where(e => e.TicketId == ticketId).ToList();
            var flagged = events.Where(e => e.EventType == WorkflowEventType.ManualAssignmentRequired).Select(e => FlaggedAnswer.TryParse(e.Note)).LastOrDefault(x => x is not null);
            Guid? flaggedEmployee = null;
            if (flagged is not null && (await f.AgentResolution.ResolveByGenesysUserIdAsync(flagged.GenesysUserId, cancellationToken)).Agent is { } mapped)
            {
                flaggedEmployee = mapped.UserId;
            }

            // Mirrors the repository: the LAST agent Genesys OFFERED the call to, from the offer events (never from the ticket owner).
            var lastOffer = events.Where(e => e.EventType == WorkflowEventType.GenesysAgentOffered)
                .Select(e => (Parsed: OfferedAgent.TryParse(e.Note), e.OccurredAtUtc)).Where(x => x.Parsed is not null)
                .OrderBy(x => x.Parsed!.OfferedAtUtc ?? x.OccurredAtUtc).ThenBy(x => x.OccurredAtUtc).LastOrDefault();
            Guid? lastOfferedEmployee = null;
            if (lastOffer.Parsed is not null && (await f.AgentResolution.ResolveByGenesysUserIdAsync(lastOffer.Parsed.GenesysUserId, cancellationToken)).Agent is { } offeredMapped)
            {
                lastOfferedEmployee = offeredMapped.UserId;
            }

            var answering = handlers.FirstOrDefault()?.HandledByUserId;
            var phoneOnly = genesys.Count > 0 && genesys.All(i => i.ChannelId == WellKnownChannels.Phone);
            DateTime? endReceived = phoneOnly && live.Count == 0 ? genesys.Max(i => i.EndReceivedAtUtc ?? i.EndedAtUtc) : null;
            var humanAnswered = t.FirstHumanResponseAtUtc is not null || answering is not null || flagged is not null
                || events.Any(e => e.EventType == WorkflowEventType.HandoffStarted);

            return new UnassignedTicketEvidence(
                t.TicketId, t.TicketNumber, t.TicketStatus, t.CurrentOwnerEmployeeId, t.CurrentDepartmentId, t.OriginatingDepartmentId, t.CreatedAtUtc, PendingApproval: false,
                HasGenesysInteraction: genesys.Count > 0, HasLiveGenesysInteraction: live.Count > 0, RequiresHumanFollowUp: interactions.Any(i => i.RequiresHumanFollowUp),
                OpenHandoff: f.Handoffs.All.Any(h => h.TicketId == ticketId && h.IsOpen), HumanEvidence: humanAnswered, AnsweringEmployeeId: answering,
                AnsweringGenesysUserId: handlers.FirstOrDefault()?.GenesysAgentUserId, PhonePickupByUnmappedAgent: false, UnmappedGenesysAgentId: null,
                LastActivityUtc: t.CreatedAtUtc, LeftCallCenter: false, ReleasedByPerson: false, GenesysIsPhoneOnly: phoneOnly, VoiceEndReceivedAtUtc: endReceived,
                FlaggedAnsweringGenesysUserId: flagged?.GenesysUserId, FlaggedAnsweringConnectedAtUtc: flagged?.ConnectedAtUtc, FlaggedAnsweringEmployeeId: flaggedEmployee,
                HumanAnswered: humanAnswered, AnsweringConnectedAtUtc: handlers.FirstOrDefault()?.ConnectedAtUtc,
                VoiceLastOfferedGenesysUserId: lastOffer.Parsed?.GenesysUserId, VoiceLastOfferedEmployeeId: lastOfferedEmployee,
                VoiceLastOfferedAtUtc: lastOffer.Parsed?.OfferedAtUtc ?? (lastOffer.Parsed is null ? null : lastOffer.OccurredAtUtc));
        }

        public Task<IReadOnlyList<UnassignedTicketEvidence>> ListOpenUnownedAsync(long afterTicketId, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UnassignedTicketEvidence>>([]);
        public Task<IReadOnlyList<UnassignedTicketEvidence>> ListActiveUnownedAsync(long afterTicketId, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UnassignedTicketEvidence>>([]);
    }

    private sealed class World
    {
        public required GenesysServiceFixture F { get; init; }
        public required Department Cs { get; init; }
        public required (Guid Id, string GenesysId)[] Agents { get; init; }
        public required VoiceCallReconciler Reconciler { get; init; }
        public Ticket Ticket(long id) => F.Tickets.All.Single(t => t.TicketId == id);
        public VoiceCallReconciliationOptions Options { get; } = new();
    }

    private sealed record Call(long TicketId, string ConversationId);

    private static World NewWorld(int agents = 3)
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true, assignOnAnswer: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var list = Enumerable.Range(1, agents)
            .Select(i => (Id: f.AddAgent($"g-agent-{i}", $"Agent {i}"), GenesysId: $"g-agent-{i}"))
            .OrderBy(a => a.Id).ToArray();
        var reconciler = new VoiceCallReconciler(new FixtureEvidence(f), f.Tickets, f.Interactions, f.AutoAssignment, f.AnsweringAgents, f.UnitOfWork, f.Audit, f.Clock);
        return new World { F = f, Cs = cs, Agents = list, Reconciler = reconciler };
    }

    private static async Task<Call> AiCallAsync(World w, string conversationId)
    {
        var r = await w.F.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(conversationId, GenesysChannel.Phone, CustomerPhone: "+971500000001", DepartmentId: w.Cs.DepartmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, r.Outcome);
        return new Call(r.Ticket!.TicketId, conversationId);
    }

    private static Task<GenesysTicketUpdateResult> EndAsync(World w, Call c) =>
        w.F.TicketUpdate.UpdateAsync(ServiceAccount, c.TicketId,
            new GenesysTicketUpdateDto(c.ConversationId, Ended: new GenesysConversationEndUpdateDto(DateTime.UtcNow.AddSeconds(-1), "CustomerDisconnect")));

    private static Task<GenesysTicketUpdateResult> ConnectAsync(World w, Call c, string genesysId, DateTime? at = null) =>
        w.F.TicketUpdate.UpdateAsync(ServiceAccount, c.TicketId,
            new GenesysTicketUpdateDto(c.ConversationId, Routing: new GenesysRoutingUpdateDto(AgentId: genesysId, ConnectedAtUtc: at ?? DateTime.UtcNow.AddSeconds(-5))));

    /// <summary>An agent Genesys OFFERS the call to (handoff request naming an available agent) - an offer, never an answer.</summary>
    private static Task<GenesysHandoffResult> OfferViaHandoffAsync(World w, Call c, string genesysId) =>
        w.F.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto(c.ConversationId, AgentAvailable: true, Mode: "HumanTakeover", AgentId: genesysId));

    /// <summary>A further offer: Genesys names another agent on the outstanding handoff (the call was transferred / offered on).</summary>
    private static Task<GenesysHandoffResult> OfferViaAssignmentAsync(World w, Call c, string genesysId) =>
        w.F.AgentHandoff.UpdateAssignmentAsync(ServiceAccount, new GenesysHandoffAssignmentDto(c.ConversationId, genesysId, "Offered agent", TicketId: c.TicketId));

    /// <summary>An update whose top-level agentId names an agent as assigned / ringing - an offer, never an answer.</summary>
    private static Task<GenesysTicketUpdateResult> OfferViaUpdateAsync(World w, Call c, string genesysId) =>
        w.F.TicketUpdate.UpdateAsync(ServiceAccount, c.TicketId, new GenesysTicketUpdateDto(c.ConversationId, AgentId: genesysId, AgentName: "Ringing agent"));

    private static Task<VoiceReconcileResult> RunAsync(World w, Call c, bool dryRun = false) => w.Reconciler.TryReconcileAsync(c.TicketId, w.Options, dryRun);

    private static void PassTheWindow(World w, int extraSeconds = 5) => w.F.Clock.Advance(w.Options.NoAnswerWindow + TimeSpan.FromSeconds(extraSeconds));

[Fact]
    public async Task CallEndedUnanswered_BeforeTheWindowNothingHappens_AfterIt_ItIsDealtToACsAgentByRoundRobin_AndTheAiAgentNoLongerOwnsIt_Once()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-1");
        await EndAsync(w, call);

        w.F.Clock.Advance(TimeSpan.FromSeconds(120));
        var early = await RunAsync(w, call);
        Assert.Equal(VoiceReconcileOutcome.Skipped, early.Outcome);
        Assert.Null(w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.NotNull(w.Ticket(call.TicketId).AiAgentOwnerEmployeeId);                       // the AI Agent still owns it inside the window

        w.F.Clock.Advance(TimeSpan.FromSeconds(200));
        var concluded = await RunAsync(w, call);

        // Nobody answered: the ticket needs a person and is dealt to the next eligible Call Center CS Agent by the shared Round Robin.
        Assert.Equal(VoiceReconcileOutcome.AssignedRoundRobin, concluded.Outcome);
        var t = w.Ticket(call.TicketId);
        Assert.Equal(w.Agents[0].Id, t.CurrentOwnerEmployeeId);
        Assert.Equal(w.Agents[0].Id, concluded.AssignedEmployeeId);
        Assert.Null(t.AiAgentOwnerEmployeeId);                                                 // a person owns it now: no Take Ownership needed to work it
        Assert.Equal(w.F.CallCenter!.DepartmentId, t.CurrentDepartmentId);
        Assert.True(w.F.InteractionFor(call.ConversationId)!.RequiresHumanFollowUp);           // concluded: the ticket needs a person (sticky)
        Assert.Single(w.F.Audit.Entries, e => e.Action == GenesysAuditActions.VoiceNoAnswerConcluded && e.EntityId == call.TicketId.ToString());
        Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == call.TicketId && a.AssignedEmployeeId == w.Agents[0].Id);   // exactly one assignment row
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "AutoAssign" && e.EntityId == call.TicketId.ToString());
        Assert.Equal(w.Agents[0].Id, w.F.Rotation.Rows.Values.Single(r => r.LastAssignedEmployeeId is not null).LastAssignedEmployeeId);
        Assert.Null(t.FirstHumanResponseAtUtc);                                                // an assignment is not a response

        var again = await RunAsync(w, call);                                                   // idempotent: owned now
        Assert.Equal(VoiceReconcileOutcome.Skipped, again.Outcome);
        Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == call.TicketId);
        Assert.Single(w.F.Audit.Entries, e => e.Action == GenesysAuditActions.VoiceNoAnswerConcluded && e.EntityId == call.TicketId.ToString());
    }

    [Fact]
    public async Task UnansweredCall_OfferedToSeveralAgents_IsNeverGivenToAnOfferedAgentBecauseOfTheOffer_ItIsDealtByRoundRobin()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-last-offer");
        await OfferViaHandoffAsync(w, call, w.Agents[0].GenesysId);                            // offered to agent 1 ...
        await OfferViaUpdateAsync(w, call, w.Agents[1].GenesysId);                             // ... then to agent 2 ...
        await OfferViaAssignmentAsync(w, call, w.Agents[2].GenesysId);                         // ... then to agent 3: nobody ever answered
        await EndAsync(w, call);
        PassTheWindow(w);

        var result = await RunAsync(w, call);

        Assert.Equal(VoiceReconcileOutcome.AssignedRoundRobin, result.Outcome);
        var t = w.Ticket(call.TicketId);
        Assert.Equal(w.Agents[0].Id, t.CurrentOwnerEmployeeId);                                // the rotation's next agent - NOT the last offered agent (agent 3)
        Assert.NotEqual(w.Agents[2].Id, t.CurrentOwnerEmployeeId);
        Assert.Null(t.FirstHumanResponseAtUtc);                                                // an offer is not an answer, and an assignment is not a response
        Assert.DoesNotContain(w.F.AssignmentHistory.Entries, h => h.TicketId == call.TicketId && h.Source == TicketAssignmentSource.GenesysLastOfferedAgent);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AssignLastOfferedAgent" && e.EntityId == call.TicketId.ToString());
        Assert.Single(w.F.Audit.Entries, e => e.Action == GenesysAuditActions.VoiceLastOfferedAgentUnavailable && e.EntityId == call.TicketId.ToString());   // diagnostic only

        Assert.Equal(VoiceReconcileOutcome.Skipped, (await RunAsync(w, call)).Outcome);        // idempotent: nothing new on the next run
        Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == call.TicketId);
    }

    [Fact]
    public async Task RepeatedOffers_AreLoggedOnce_AndNeverAssignAnyone()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-abab");
        await OfferViaHandoffAsync(w, call, w.Agents[0].GenesysId);
        await OfferViaAssignmentAsync(w, call, w.Agents[1].GenesysId);
        await OfferViaAssignmentAsync(w, call, w.Agents[1].GenesysId);                         // a redelivery of the same offer: not a new offer
        await OfferViaAssignmentAsync(w, call, w.Agents[0].GenesysId);                         // back to agent 1 - the latest offer
        Assert.Equal(3, w.F.WorkflowEvents.All.Count(e => e.TicketId == call.TicketId && e.EventType == WorkflowEventType.GenesysAgentOffered));
        await EndAsync(w, call);
        PassTheWindow(w);

        Assert.Equal(VoiceReconcileOutcome.AssignedRoundRobin, (await RunAsync(w, call)).Outcome);
        Assert.Equal(w.Agents[0].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task ASingleOffer_NeverAssignsTheOfferedAgent()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-not-owner");
        await OfferViaHandoffAsync(w, call, w.Agents[1].GenesysId);                            // the only offer: agent 2
        await EndAsync(w, call);
        PassTheWindow(w);

        Assert.Equal(VoiceReconcileOutcome.AssignedRoundRobin, (await RunAsync(w, call)).Outcome);
        Assert.Equal(w.Agents[0].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);          // the rotation's next agent, not the offered agent 2
    }

    [Fact]
    public async Task AnAgentWhoAnswersAfterBeingOffered_OwnsTheTicket_AndTheReconcilerHasNothingToDo()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-offered-then-answered");
        await OfferViaHandoffAsync(w, call, w.Agents[0].GenesysId);                            // offered to agent 1 ...
        await ConnectAsync(w, call, w.Agents[1].GenesysId);                                    // ... but agent 2 actually answered
        await EndAsync(w, call);
        PassTheWindow(w);

        Assert.Equal(VoiceReconcileOutcome.Skipped, (await RunAsync(w, call)).Outcome);
        Assert.Equal(w.Agents[1].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);          // the agent who ANSWERED, not the last one offered
    }

    [Fact]
    public async Task AnUnmappedOfferedAgent_IsOnlyAudited_AndTheTicketIsDealtToAnEligibleAgentByRoundRobin()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-offered-unmapped");
        await OfferViaHandoffAsync(w, call, w.Agents[0].GenesysId);
        await OfferViaAssignmentAsync(w, call, "g-offered-ghost");                            // the LAST offer names an id that maps to nobody
        await EndAsync(w, call);
        PassTheWindow(w);

        var first = await RunAsync(w, call);

        Assert.Equal(VoiceReconcileOutcome.AssignedRoundRobin, first.Outcome);
        Assert.Equal(w.Agents[0].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.Single(w.F.Audit.Entries, e => e.Action == GenesysAuditActions.VoiceLastOfferedAgentUnavailable && e.EntityId == call.TicketId.ToString());

        w.F.AddAgent("g-offered-ghost", "Fixed Agent");                                        // the mapping is corrected later: an OFFER still proves nothing
        Assert.Equal(VoiceReconcileOutcome.Skipped, (await RunAsync(w, call)).Outcome);
        Assert.Equal(w.Agents[0].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task ALastOfferedAgentWhoIsNotACsAgent_IsNotAssigned_AndTheNextCsAgentIsDealtIt()
    {
        var w = NewWorld();
        var employee = w.F.AddAgent("g-dept-employee", "Dept Employee", roles: Roles.DepartmentEmployee);   // mapped, but not a CS Agent
        var call = await AiCallAsync(w, "r-offered-ineligible");
        await OfferViaHandoffAsync(w, call, "g-dept-employee");
        await EndAsync(w, call);
        PassTheWindow(w);

        Assert.Equal(VoiceReconcileOutcome.AssignedRoundRobin, (await RunAsync(w, call)).Outcome);
        Assert.NotEqual(employee, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);             // a Department Employee is never dealt Call Center work
        Assert.Equal(w.Agents[0].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);          // a CS Agent is
    }

    [Fact]
    public async Task ADigitalConversation_RecordsNoOfferEvents_AndIsNotThisJobsBusiness()
    {
        var w = NewWorld();
        var chat = await w.F.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto("r-chat-offer", GenesysChannel.WhatsApp, CustomerPhone: "+971500000001", DepartmentId: w.Cs.DepartmentId));
        var ticketId = chat.Ticket!.TicketId;

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, ticketId, new GenesysTicketUpdateDto("r-chat-offer", AgentId: w.Agents[0].GenesysId, AgentName: "Ringing"));

        Assert.DoesNotContain(w.F.WorkflowEvents.All, e => e.TicketId == ticketId && e.EventType == WorkflowEventType.GenesysAgentOffered);   // digital behaviour is unchanged
    }

[Fact]
    public async Task SeveralUnansweredCalls_AreDealtToConsecutiveCsAgents_OneEach_NoDuplicates()
    {
        var w = NewWorld();
        var calls = new List<Call>();
        for (var i = 0; i < 4; i++)
        {
            var c = await AiCallAsync(w, $"r-many-{i}");
            await EndAsync(w, c);
            calls.Add(c);
        }

        PassTheWindow(w);
        foreach (var c in calls) Assert.Equal(VoiceReconcileOutcome.AssignedRoundRobin, (await RunAsync(w, c)).Outcome);

        Assert.Equal(
            [w.Agents[0].Id, w.Agents[1].Id, w.Agents[2].Id, w.Agents[0].Id],
            calls.Select(c => w.Ticket(c.TicketId).CurrentOwnerEmployeeId!.Value).ToArray());   // the shared cursor deals consecutive slots
        Assert.All(calls, c => Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == c.TicketId));
    }

[Fact]
    public async Task AnOpenHandoffThatNamedNoAgent_DoesNotBlockTheConclusion_OnceTheCallEnded_ItIsDealtByRoundRobin()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-handoff");
        await w.F.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto(call.ConversationId, AgentAvailable: false, Mode: "HumanTakeover"));
        Assert.Null(w.Ticket(call.TicketId).CurrentOwnerEmployeeId);                           // handoff time: no Round Robin

        var beforeEnd = await RunAsync(w, call);
        Assert.Equal(VoiceReconcileOutcome.Skipped, beforeEnd.Outcome);                        // a human answer is still pending: the call has not ended
        Assert.Null(w.Ticket(call.TicketId).CurrentOwnerEmployeeId);

        await EndAsync(w, call);
        PassTheWindow(w);
        var result = await RunAsync(w, call);

        Assert.Equal(VoiceReconcileOutcome.AssignedRoundRobin, result.Outcome);                // nobody answered after the call ended: dealt to a CS Agent
        Assert.Equal(w.Agents[0].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.Null(w.Ticket(call.TicketId).AiAgentOwnerEmployeeId);
    }

    [Fact]
    public async Task AnAnswerThatArrivesAfterEnded_InsideTheWindow_BeatsRoundRobin_AndTheReconcilerThenHasNothingToDo()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-late-answer");
        await EndAsync(w, call);
        w.F.Clock.Advance(TimeSpan.FromSeconds(100));

        await ConnectAsync(w, call, w.Agents[2].GenesysId);                                    // routing.agentId arrives after ended
        PassTheWindow(w);
        var result = await RunAsync(w, call);

        Assert.Equal(VoiceReconcileOutcome.Skipped, result.Outcome);
        Assert.Equal(w.Agents[2].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);          // the person who answered - never the next in the rotation
        Assert.All(w.F.Rotation.Rows.Values, r => Assert.Null(r.LastAssignedEmployeeId));
    }

    [Fact]
    public async Task ClosedByAiInsideTheWindow_AfterEnded_ClosesTheTicket_AndTheReconcilerNeverAssignsIt()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-ai-closes");
        await EndAsync(w, call);
        w.F.Clock.Advance(TimeSpan.FromSeconds(60));

        var closed = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, call.TicketId, new GenesysTicketUpdateDto(call.ConversationId, ClosedByAi: true));
        Assert.True(closed.ClosedByAi);
        PassTheWindow(w);
        var result = await RunAsync(w, call);

        Assert.Equal(VoiceReconcileOutcome.Skipped, result.Outcome);
        Assert.Equal(TicketStatus.Closed, w.Ticket(call.TicketId).TicketStatus);
        Assert.Null(w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.All(w.F.Rotation.Rows.Values, r => Assert.Null(r.LastAssignedEmployeeId));
    }

    [Fact]
    public async Task ClosedByAiAfterTheConclusion_IsRefused_BecauseAPersonNowOwnsAFlaggedTicket()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-too-late");
        await EndAsync(w, call);
        PassTheWindow(w);
        await RunAsync(w, call);

        var late = await w.F.TicketUpdate.UpdateAsync(ServiceAccount, call.TicketId, new GenesysTicketUpdateDto(call.ConversationId, ClosedByAi: true));

        Assert.False(late.ClosedByAi);
        Assert.NotEqual(TicketStatus.Closed, w.Ticket(call.TicketId).TicketStatus);
    }

[Fact]
    public async Task WithNoEligibleCallCenterAgent_TheCallIsQueuedAndFlagged_RetriedEveryRunWritingNothing_AndDealtAsSoonAsAnAgentIsEligible()
    {
        var w = NewWorld(agents: 0);
        var call = await AiCallAsync(w, "r-nobody");
        await EndAsync(w, call);
        PassTheWindow(w);

        var first = await RunAsync(w, call);
        Assert.Equal(VoiceReconcileOutcome.LeftUnassignedForFollowUp, first.Outcome);
        Assert.Null(w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.NotNull(w.Ticket(call.TicketId).AiAgentOwnerEmployeeId);                            // still the recorded owner until a person can take it
        Assert.Equal(w.F.CallCenter!.DepartmentId, w.Ticket(call.TicketId).CurrentDepartmentId);   // visible in the Call Center queue
        var writes = w.F.Audit.Entries.Count;

        for (var i = 0; i < 3; i++)                                                                // every retry: nothing eligible, nothing written
        {
            Assert.Equal(VoiceReconcileOutcome.QueuedNoEligibleAgent, (await RunAsync(w, call)).Outcome);
        }

        Assert.Equal(writes, w.F.Audit.Entries.Count);
        Assert.DoesNotContain(w.F.TicketAssignments.Added, a => a.TicketId == call.TicketId);

        var hire = w.F.AddAgent("g-late-hire", "Late Hire");
        var retried = await RunAsync(w, call);

        Assert.Equal(VoiceReconcileOutcome.AssignedRoundRobin, retried.Outcome);                   // the retry picks it up
        Assert.Equal(hire, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.Null(w.Ticket(call.TicketId).AiAgentOwnerEmployeeId);
        Assert.Single(w.F.Audit.Entries, e => e.Action == GenesysAuditActions.VoiceNoAnswerConcluded && e.EntityId == call.TicketId.ToString());   // concluded once
    }

    [Fact]
    public async Task AnInactiveOrNonCsAgentIsNeverDealt_AndAnOldStuckTicketOutsideTheLookbackIsRecovered()
    {
        var w = NewWorld(agents: 0);
        var deptEmployee = w.F.AddAgent("g-dept-only", "Dept Employee", roles: Roles.DepartmentEmployee);
        var call = await AiCallAsync(w, "r-stuck-old");
        await EndAsync(w, call);
        PassTheWindow(w);
        Assert.Equal(VoiceReconcileOutcome.LeftUnassignedForFollowUp, (await RunAsync(w, call)).Outcome);   // only a Department Employee exists: not eligible in the Call Center
        Assert.Null(w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.NotEqual(deptEmployee, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);

        w.F.Clock.Advance(TimeSpan.FromDays(10));                                                  // the ticket has been stuck for days (outside the 24 h look-back)
        var csAgent = w.F.AddAgent("g-cs-agent", "CS Agent");
        var recovered = await RunAsync(w, call);

        Assert.Equal(VoiceReconcileOutcome.AssignedRoundRobin, recovered.Outcome);
        Assert.Equal(csAgent, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task DryRun_ClassifiesAndWritesNothing()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-dry");
        await EndAsync(w, call);
        PassTheWindow(w);
        var writes = w.F.Audit.Entries.Count;

        var result = await RunAsync(w, call, dryRun: true);

        Assert.Equal(VoiceReconcileOutcome.WouldAct, result.Outcome);
        Assert.Null(w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.Equal(writes, w.F.Audit.Entries.Count);
        Assert.False(w.F.InteractionFor(call.ConversationId)!.RequiresHumanFollowUp);
    }

    [Fact]
    public async Task ACustomWindow_IsHonoured()
    {
        var w = NewWorld();
        w.Options.NoAnswerWindowSeconds = 30;
        var call = await AiCallAsync(w, "r-30s");
        await EndAsync(w, call);

        w.F.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(VoiceReconcileOutcome.Skipped, (await RunAsync(w, call)).Outcome);
        w.F.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(VoiceReconcileOutcome.AssignedRoundRobin, (await RunAsync(w, call)).Outcome);   // nobody answered: dealt to a CS Agent
    }

    [Fact]
    public async Task AClosedTicket_IsNeverAssigned_ByTheReconciler()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-closed");
        await EndAsync(w, call);
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, call.TicketId, new GenesysTicketUpdateDto(call.ConversationId, ClosedByAi: true));
        PassTheWindow(w);

        var result = await RunAsync(w, call);

        Assert.Equal(VoiceReconcileOutcome.Skipped, result.Outcome);
        Assert.Equal(TicketStatus.Closed, w.Ticket(call.TicketId).TicketStatus);
        Assert.DoesNotContain(w.F.TicketAssignments.Added, a => a.TicketId == call.TicketId);
    }

    // ---- unmapped answering agent --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnmappedAnsweringAgent_StaysQueuedAndFlagged_NeverRoundRobin_UntilTheMappingIsCorrected_ThenTheTicketGoesToThem()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-unmapped");
        var connectedAt = DateTime.UtcNow.AddMinutes(-2);
        await ConnectAsync(w, call, "g-brand-new-agent", connectedAt);                         // a person answered; the id maps to nobody yet
        await EndAsync(w, call);
        PassTheWindow(w);

        for (var i = 0; i < 3; i++)                                                            // every run: reported, never dealt
        {
            var pending = await RunAsync(w, call);
            Assert.Equal(VoiceReconcileOutcome.FlaggedMappingPending, pending.Outcome);
        }

        Assert.Null(w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.All(w.F.Rotation.Rows.Values, r => Assert.Null(r.LastAssignedEmployeeId));
        Assert.Single(w.F.WorkflowEvents.All, e => e.TicketId == call.TicketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);   // flagged once
        Assert.Null(w.Ticket(call.TicketId).FirstHumanResponseAtUtc);

        var corrected = w.F.AddAgent("g-brand-new-agent", "Now Mapped");                       // the administrator fixes the mapping
        var fixedRun = await RunAsync(w, call);

        Assert.Equal(VoiceReconcileOutcome.AssignedToAnsweringAgent, fixedRun.Outcome);
        var t = w.Ticket(call.TicketId);
        Assert.Equal(corrected, t.CurrentOwnerEmployeeId);
        Assert.Equal(corrected, t.OriginalHumanAgentId);                                       // persisted
        Assert.Equal(connectedAt, t.FirstHumanResponseAtUtc);                                  // dated at the REAL connect time, not at the reconciliation
        Assert.All(w.F.Rotation.Rows.Values, r => Assert.Null(r.LastAssignedEmployeeId));
    }

    [Fact]
    public async Task ATicketClosedBeforeTheMappingIsCorrected_IsNeverReconciled()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-unmapped-closed");
        await ConnectAsync(w, call, "g-ghost");
        var ticket = w.Ticket(call.TicketId);
        ticket.AssignTo(w.Agents[0].Id);                                                       // a person is given it, starts working it ...
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, null);
        ticket.Close();                                                                        // ... and closes it
        w.F.AddAgent("g-ghost", "Fixed Later");

        var result = await RunAsync(w, call);

        Assert.Equal(VoiceReconcileOutcome.Skipped, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);                                            // closed: ownership is never changed afterwards
        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
    }

    // ---- explicit workflow department ----------------------------------------------------------------------------------------------

[Fact]
    public async Task ExplicitWorkflowDepartment_IsKept_AndTheCallIsDealtByThatDepartmentsOwnRoundRobin_NeverToTheCallCenterAgents()
    {
        var w = NewWorld();
        var legal = w.F.Departments.AddDepartment("Legal", "LEGAL");
        var lawyer = Guid.NewGuid();
        w.F.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(lawyer, legal.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        w.F.UserRoles.Add(lawyer, Roles.DepartmentEmployee);
        w.F.Accounts!.Add(lawyer, "lawyer.one", [Roles.DepartmentEmployee]);

        var unoffered = await AiCallAsync(w, "r-legal-unoffered");
        w.Ticket(unoffered.TicketId).TransferToDepartment(legal.DepartmentId, keepAiAgentOwner: true);   // an explicit workflow (request type) put it in Legal
        await EndAsync(w, unoffered);
        var offeredCall = await AiCallAsync(w, "r-legal-offered");
        w.Ticket(offeredCall.TicketId).TransferToDepartment(legal.DepartmentId, keepAiAgentOwner: true);
        await OfferViaHandoffAsync(w, offeredCall, w.Agents[1].GenesysId);
        await EndAsync(w, offeredCall);
        PassTheWindow(w);

        var a = await RunAsync(w, unoffered);
        var b = await RunAsync(w, offeredCall);

        Assert.Equal(legal.DepartmentId, w.Ticket(unoffered.TicketId).CurrentDepartmentId);    // never pulled into the Call Center
        Assert.Equal(VoiceReconcileOutcome.AssignedRoundRobin, a.Outcome);                     // the department's own rotation
        Assert.Equal(lawyer, w.Ticket(unoffered.TicketId).CurrentOwnerEmployeeId);
        Assert.Null(w.Ticket(unoffered.TicketId).AiAgentOwnerEmployeeId);

        Assert.Equal(legal.DepartmentId, w.Ticket(offeredCall.TicketId).CurrentDepartmentId);
        Assert.Equal(VoiceReconcileOutcome.AssignedRoundRobin, b.Outcome);                     // an offer is not a confirmed connection: department rotation, not the offered agent
        Assert.Equal(lawyer, w.Ticket(offeredCall.TicketId).CurrentOwnerEmployeeId);
    }

    [Fact]
    public void AnAgentIdOnTheInteractionAlone_IsNotEvidence_AnEndedUnansweredCallIsStillConcluded() =>
        Assert.Equal(VoiceReconcileClass.NoAnswerConcluded, C(Ev(endReceived: Now.AddHours(-1), phoneUnmapped: true)).Class);

    [Fact]
    public void ThePhoneOnlyTicket_IsExcludedFromTheCallCenterReconciliation_WhateverItsEvidence()
    {
        foreach (var e in new[]
        {
            Ev(endReceived: Now.AddHours(-1)),                                         // ended, unanswered
            Ev(endReceived: Now.AddHours(-1), humanAnswered: true),                    // answered
            Ev(endReceived: null),                                                     // live
            Ev(endReceived: Now.AddHours(-1), flaggedId: "g-x"),                       // flagged
        })
        {
            var legacy = UnassignedTicketClassifier.Classify(e, callCenterDepartmentId: 99, Now, staleAiAfter: TimeSpan.FromHours(1), actOnStaleAi: true);
            Assert.Equal(new UnassignedTicketClassification(UnassignedTicketClass.HandledByVoiceReconciler, UnassignedTicketAction.None), legacy);
        }

        // ... while a ticket with any digital interaction is still the legacy job's.
        Assert.NotEqual(UnassignedTicketClass.HandledByVoiceReconciler,
            UnassignedTicketClassifier.Classify(Ev(phoneOnly: false, endReceived: Now.AddHours(-1)), 99, Now).Class);
    }

    // ---- offered / assigned but never answered: the LAST agent offered gets it (never Round Robin) ---------------------------------------------------------------

    [Theory]
    [InlineData(false)]   // the offered agent id maps to nobody
    [InlineData(true)]    // the offered agent is a mapped, eligible Call Center agent
    public async Task ANamedAgentOfferedTheCall_WhoNeverAnswered_IsNeverAssignedBecauseOfTheOffer_MappedOrNot_TheRotationDealsIt(bool offeredIsMapped)
    {
        var w = NewWorld();
        var offered = offeredIsMapped ? w.Agents[1] : (Id: Guid.Empty, GenesysId: "g-offered-ghost");
        var call = await AiCallAsync(w, "r-offered-" + offeredIsMapped);
        await w.F.AgentHandoff.RequestAsync(
            ServiceAccount, new GenesysHandoffRequestDto(call.ConversationId, AgentAvailable: true, Mode: "HumanTakeover", AgentId: offered.GenesysId));
        Assert.Equal(offered.GenesysId, w.F.InteractionFor(call.ConversationId)!.GenesysAgentId);   // the scenario: the id IS stored on the interaction
        Assert.DoesNotContain(w.F.Handlers.All, h => h.TicketId == call.TicketId);                   // ... but nobody connected
        Assert.Null(w.Ticket(call.TicketId).CurrentOwnerEmployeeId);                                 // an offer assigns nothing at handoff time
        await EndAsync(w, call);
        PassTheWindow(w);

        var result = await RunAsync(w, call);

        Assert.Null(w.Ticket(call.TicketId).FirstHumanResponseAtUtc);                                // an offer is not a response
        Assert.Equal(VoiceReconcileOutcome.AssignedRoundRobin, result.Outcome);
        Assert.Equal(w.Agents[0].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);                // the rotation's next agent: the offer decided nothing (agent 2 was the offered one)
        Assert.NotEqual(offered.Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.Equal(w.Agents[0].Id, w.Ticket(call.TicketId).OriginalHumanAgentId);                  // the first CS Agent dealt a Call Center ticket is its Original Human Agent
    }

    [Fact]
    public async Task ARingingEvent_ThatNamesAnAgent_IsNotAnAnswer()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-ringing");
        // An update that names an agent only as assigned / ringing: top-level agentId and handoff.assignedAgentId, no routing.agentId (the connected agent).
        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, call.TicketId, new GenesysTicketUpdateDto(
            call.ConversationId, AgentId: w.Agents[1].GenesysId, AgentName: "Ringing Agent",
            Handoff: new GenesysHandoffUpdateDto(Required: true, Mode: "HumanTakeover", AssignedAgentId: w.Agents[1].GenesysId)));
        await EndAsync(w, call);
        PassTheWindow(w);

        Assert.Null(w.Ticket(call.TicketId).CurrentOwnerEmployeeId);                           // ringing assigned nothing
        Assert.Null(w.Ticket(call.TicketId).OriginalHumanAgentId);
        // The call ended unanswered: ringing is not an answer, so the ringing agent is not given the ticket - the rotation deals it.
        Assert.Equal(VoiceReconcileOutcome.AssignedRoundRobin, (await RunAsync(w, call)).Outcome);
        Assert.Equal(w.Agents[0].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.NotEqual(w.Agents[1].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.Null(w.Ticket(call.TicketId).FirstHumanResponseAtUtc);                          // an assignment is not a response
    }

    // ---- a CONNECTED agent whose mapping is missing ------------------------------------------------------------------------------------

    [Fact]
    public async Task ADirectCallPickedUpByAnUnmappedAgent_IsFlaggedAtCreation_ThenAssignedWithTheRealConnectTime_WhenTheMappingIsFixed()
    {
        var w = NewWorld();
        var connectedAt = DateTime.UtcNow.AddMinutes(-4);
        var r = await w.F.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto("r-pickup-unmapped", GenesysChannel.Phone, CustomerPhone: "+971500000001", DepartmentId: w.Cs.DepartmentId,
                AgentId: "g-new-hire", ConnectedAtUtc: connectedAt));
        var call = new Call(r.Ticket!.TicketId, "r-pickup-unmapped");
        Assert.Null(w.Ticket(call.TicketId).CurrentOwnerEmployeeId);                                 // not guessed, not Round Robin
        Assert.Single(w.F.WorkflowEvents.All, e => e.TicketId == call.TicketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);
        await EndAsync(w, call);
        PassTheWindow(w);
        Assert.Equal(VoiceReconcileOutcome.FlaggedMappingPending, (await RunAsync(w, call)).Outcome);   // the window never turns a real answer into Round Robin

        var hire = w.F.AddAgent("g-new-hire", "New Hire");
        var fixedRun = await RunAsync(w, call);

        Assert.Equal(VoiceReconcileOutcome.AssignedToAnsweringAgent, fixedRun.Outcome);
        Assert.Equal(hire, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.Equal(hire, w.Ticket(call.TicketId).OriginalHumanAgentId);
    }

    // ---- delayed and out-of-order connected events -------------------------------------------------------------------------------------

    [Fact]
    public async Task ADelayedConnectEvent_IsDatedAtTheRealConnectTime_NotTheTimeItArrived()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-delayed");
        var connectedAt = DateTime.UtcNow.AddMinutes(-12);                                          // the answer happened 12 minutes before the event reached us

        await ConnectAsync(w, call, w.Agents[1].GenesysId, connectedAt);

        Assert.Equal(connectedAt, w.Ticket(call.TicketId).FirstHumanResponseAtUtc);
        var assignment = Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == call.TicketId);
        Assert.Equal(w.Ticket(call.TicketId).CreatedAtUtc, assignment.AssignedAtUtc);               // dated at the connect - but never before the ticket existed
    }

    [Fact]
    public async Task ALateConnectEvent_AfterTheNoAnswerWindowConcluded_ReplacesTheProvisionalOwner_WithTheAgentWhoActuallyAnswered()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-late-after-offer");
        await OfferViaHandoffAsync(w, call, w.Agents[0].GenesysId);
        await EndAsync(w, call);
        PassTheWindow(w);
        await RunAsync(w, call);
        Assert.Equal(w.Agents[0].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);                // the window concluded: a CS Agent was dealt it (a provisional, automatic owner)
        var answered = DateTime.UtcNow.AddMinutes(-8);

        await ConnectAsync(w, call, w.Agents[2].GenesysId, answered);                                // ... then the real answer event arrives

        Assert.Equal(w.Agents[2].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);                // the late confirmed connection assigns the agent who actually answered
        Assert.Equal(answered, w.Ticket(call.TicketId).FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task OutOfOrderConnects_TheAgentSeenFirstKeepsIt_AnOlderDuplicateOrAnEarlierOtherAgentNeverTakesItOver()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-ooo");
        var t = DateTime.UtcNow.AddMinutes(-10);
        await ConnectAsync(w, call, w.Agents[0].GenesysId, t.AddMinutes(5));

        await ConnectAsync(w, call, w.Agents[1].GenesysId, t.AddMinutes(3));                         // an earlier event for another agent, arriving late
        await ConnectAsync(w, call, w.Agents[0].GenesysId, t.AddMinutes(1));                         // an older duplicate of the first agent
        await ConnectAsync(w, call, w.Agents[0].GenesysId, t.AddMinutes(5));                         // the exact duplicate

        Assert.Equal(w.Agents[0].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == call.TicketId);
        Assert.Equal(w.Agents[0].Id, w.Ticket(call.TicketId).OriginalHumanAgentId);
        Assert.Equal(t.AddMinutes(5), w.Ticket(call.TicketId).FirstHumanResponseAtUtc);              // first processed answer; write-once, never moved
    }

    // ---- first-response SLA through the answering-agent service ---------------------------------------------------------------------------

    [Fact]
    public async Task AHandoffAndTheConnectedAgentInTheSameUpdate_StillRecordsTheFirstResponseAtTheConnectTime()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-same-update");
        var connectedAt = DateTime.UtcNow.AddMinutes(-2);

        await w.F.TicketUpdate.UpdateAsync(ServiceAccount, call.TicketId, new GenesysTicketUpdateDto(
            call.ConversationId, Handoff: new GenesysHandoffUpdateDto(Required: true, Mode: "HumanTakeover"),
            Routing: new GenesysRoutingUpdateDto(AgentId: w.Agents[1].GenesysId, ConnectedAtUtc: connectedAt)));

        Assert.Equal(w.Agents[1].Id, w.Ticket(call.TicketId).CurrentOwnerEmployeeId);
        Assert.Equal(connectedAt, w.Ticket(call.TicketId).FirstHumanResponseAtUtc);
    }

    [Fact]
    public async Task TheReconcilerAssigningAFixedMapping_RecordsTheFirstResponse_OnlyAtTheFlaggedConnectTime_AndOnce()
    {
        var w = NewWorld();
        var call = await AiCallAsync(w, "r-fr-fixed");
        var connectedAt = DateTime.UtcNow.AddMinutes(-6);
        await ConnectAsync(w, call, "g-fr-hire", connectedAt);
        Assert.Null(w.Ticket(call.TicketId).FirstHumanResponseAtUtc);
        w.F.AddAgent("g-fr-hire", "FR Hire");

        await RunAsync(w, call);
        await RunAsync(w, call);

        Assert.Equal(connectedAt, w.Ticket(call.TicketId).FirstHumanResponseAtUtc);
        Assert.Single(w.F.Audit.Entries, e => e.Action == "RecordFirstResponse" && e.EntityId == call.TicketId.ToString());
    }
}
