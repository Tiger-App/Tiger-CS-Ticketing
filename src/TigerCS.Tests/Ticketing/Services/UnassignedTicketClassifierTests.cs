using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>The taxonomy behind "why does this Open ticket have no employee owner, and may TigerCS give it one?".</summary>
public class UnassignedTicketClassifierTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
    private const int CallCenter = 1, Leasing = 2;

    private static UnassignedTicketEvidence Ev(
        TicketStatus status = TicketStatus.Open, Guid? owner = null, int department = Leasing, bool approval = false, bool genesys = false,
        bool live = false, bool humanFlag = false, bool handoff = false, bool humanEvidence = false, Guid? answered = null, bool phoneUnmapped = false,
        double silentHours = 0.1, bool left = false, bool released = false, int origin = Leasing) =>
        new(1, "TG-LCS-20261102-0021", status, owner, department, origin, Now.AddDays(-2), approval, genesys, live, humanFlag, handoff, humanEvidence,
            answered, answered is null ? null : "gen-1", phoneUnmapped, phoneUnmapped ? "gen-x" : null, Now.AddHours(-silentHours), left, released);

    private static UnassignedTicketClassification C(UnassignedTicketEvidence e, TimeSpan? stale = null, bool actOnStale = false, int? cc = CallCenter) =>
        UnassignedTicketClassifier.Classify(e, cc, Now, stale, actOnStale);

    [Theory]
    [InlineData(TicketStatus.InProgress)]
    [InlineData(TicketStatus.PendingCustomer)]
    [InlineData(TicketStatus.Resolved)]
    [InlineData(TicketStatus.Closed)]
    public void NonOpenTickets_AreNeverTouched(TicketStatus status) =>
        Assert.Equal(new UnassignedTicketClassification(UnassignedTicketClass.NotOpen, UnassignedTicketAction.None), C(Ev(status, humanFlag: true)));

    [Fact]
    public void AnOwnedTicket_IsNeverRedistributed_WhateverElseIsTrue() =>
        Assert.Equal(UnassignedTicketAction.None, C(Ev(owner: Guid.NewGuid(), humanFlag: true, answered: Guid.NewGuid())).Action);

    [Fact]
    public void PendingApproval_IsHeld() =>
        Assert.Equal(UnassignedTicketClass.PendingApproval, C(Ev(approval: true, humanFlag: true)).Class);

    [Fact]
    public void ActiveAi_IsPreserved_WhileRecentlyActive_AndEvenWhenSilent_IfNoStaleThreshold()
    {
        Assert.Equal(UnassignedTicketAction.None, C(Ev(genesys: true, live: true, silentHours: 0.05), TimeSpan.FromHours(24)).Action);
        Assert.Equal(UnassignedTicketClass.ActiveAiConversation, C(Ev(genesys: true, live: true, silentHours: 5000)).Class);
    }

    [Fact]
    public void StaleAi_NoEndEvent_IsReported_ButOnlyActedOnWhenExplicitlyEnabled()
    {
        var stale = Ev(genesys: true, live: true, silentHours: 72);
        Assert.Equal(new UnassignedTicketClassification(UnassignedTicketClass.StaleAiNoEndEvent, UnassignedTicketAction.None), C(stale, TimeSpan.FromHours(24)));
        Assert.Equal(UnassignedTicketAction.RoundRobinCallCenter, C(stale, TimeSpan.FromHours(24), actOnStale: true).Action);
    }

    [Fact]
    public void CompletedAiTicket_IsPreserved() =>
        Assert.Equal(new UnassignedTicketClassification(UnassignedTicketClass.AiEndedNoHumanNeed, UnassignedTicketAction.None), C(Ev(genesys: true, live: false)));

    [Fact]
    public void FailedGenesysRouting_HumanFlagOrOpenHandoffOutsideCallCenter_IsRoutedByRoundRobin()
    {
        Assert.Equal(new UnassignedTicketClassification(UnassignedTicketClass.HumanRequiredNotRouted, UnassignedTicketAction.RoundRobinCallCenter), C(Ev(genesys: true, live: true, humanFlag: true)));
        Assert.Equal(UnassignedTicketClass.HumanRequiredNotRouted, C(Ev(genesys: true, live: true, handoff: true)).Class);
        Assert.Equal(UnassignedTicketClass.HumanRequiredNotRouted, C(Ev(genesys: true, live: true, humanEvidence: true)).Class);
    }

    [Fact]
    public void InCallCenterWithNoOwner_Human_IsDealtByRoundRobin() =>
        Assert.Equal(new UnassignedTicketClassification(UnassignedTicketClass.HumanRequiredInCallCenterUnassigned, UnassignedTicketAction.RoundRobinCallCenter),
            C(Ev(department: CallCenter, genesys: true, live: true, humanFlag: true)));

    [Fact]
    public void AMappedAgentWhoAnswered_OwnsIt_EvenWhenNothingElseFlagsHumanWork()
    {
        var agent = Guid.NewGuid();
        var result = C(Ev(genesys: true, live: true, answered: agent));
        Assert.Equal(new UnassignedTicketClassification(UnassignedTicketClass.HumanAnsweredNotAssigned, UnassignedTicketAction.AssignToAnsweringAgent), result);
    }

    [Fact]
    public void APhoneCallAnsweredByAnUnmappedAgent_IsHumanWork_RoundRobin() =>
        Assert.Equal(new UnassignedTicketClassification(UnassignedTicketClass.HumanAnsweredAgentNotMapped, UnassignedTicketAction.RoundRobinCallCenter),
            C(Ev(genesys: true, live: true, phoneUnmapped: true)));

    [Fact]
    public void ManualOrIntegrationTicket_WithNoGenesysConversation_IsAlwaysHumanWork()
    {
        Assert.Equal(new UnassignedTicketClassification(UnassignedTicketClass.ManualOrIntegrationNotRouted, UnassignedTicketAction.RoundRobinCallCenter), C(Ev()));
        Assert.Equal(UnassignedTicketClass.HumanRequiredInCallCenterUnassigned, C(Ev(department: CallCenter)).Class);
    }

    [Fact]
    public void TicketTheCallCenterTransferredOnward_IsNeverPulledBack()
    {
        Assert.Equal(new UnassignedTicketClassification(UnassignedTicketClass.TransferredOutOfCallCenter, UnassignedTicketAction.None), C(Ev(left: true, humanFlag: true)));
        Assert.Equal(UnassignedTicketAction.None, C(Ev(left: true, answered: Guid.NewGuid())).Action);
    }

    [Fact]
    public void ReleasedToTheQueueByAPerson_InTheCallCenter_IsLeftAlone() =>
        Assert.Equal(UnassignedTicketClass.ReleasedByPerson, C(Ev(department: CallCenter, released: true, humanFlag: true)).Class);

    [Fact]
    public void WithNoCallCenterConfigured_NothingIsTransferredOut_ButClassificationStillWorks() =>
        Assert.Equal(UnassignedTicketClass.HumanRequiredNotRouted, C(Ev(genesys: true, live: true, humanFlag: true), cc: null).Class);
}
