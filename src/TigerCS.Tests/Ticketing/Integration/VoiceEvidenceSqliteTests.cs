using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// The voice evidence as real SQL over the real schema (the same query shape runs on SQL Server): which tickets the voice reconciler even looks at, when a call's end was
/// RECEIVED, whether a ticket is phone-only, and a flagged answering agent re-resolved against the CURRENT mapping.
/// </summary>
public sealed class VoiceEvidenceSqliteTests : IDisposable
{
    private readonly DashboardSqliteFixture _db = new(enforceForeignKeys: false);
    private static DateTime Now => DashboardSqliteFixture.Now;

    public void Dispose() => _db.Dispose();

    private static TicketInteraction Call(Ticket ticket, byte channel, string conv, DateTime started) =>
        TicketInteraction.CreateFromGenesys(
            ticket.TicketId, channel, "+971500000000", conv, calledNumber: null, genesysQueueId: null, genesysQueueName: null, genesysAgentId: null, genesysAgentName: null,
            interactionStartedAtUtc: started, direction: "Inbound", createdAtUtc: started, isOriginatingInteraction: true, genesysServiceRequestId: Guid.NewGuid().ToString("N")[..8]);

    private (long Phone, long Digital, long Old, long Owned, long Flagged, long Live) Seed(Guid flaggedAgentUser, string flaggedGenesysId)
    {
        using var context = _db.CreateContext();
        context.Departments.Add(new Department("Call Center", "CC"));
        context.Users.Add(new TigerCS.Infrastructure.Identity.ApplicationUser
        {
            Id = flaggedAgentUser, UserName = "fixed@example.test", NormalizedUserName = "FIXED@EXAMPLE.TEST", Email = "fixed@example.test",
            SecurityStamp = Guid.NewGuid().ToString(), GenesysUserId = flaggedGenesysId
        });
        context.SaveChanges();

        var phone = _db.AddTicket(context, _db.CustomerServiceId);
        var phoneCall = Call(phone, WellKnownChannels.Phone, "v-recent", Now.AddMinutes(-30));
        phoneCall.End(Now.AddMinutes(-20), "CustomerDisconnect", receivedAtUtc: Now.AddMinutes(-10));
        context.TicketInteractions.Add(phoneCall);

        var digital = _db.AddTicket(context, _db.CustomerServiceId);
        var chat = Call(digital, WellKnownChannels.Website, "d-recent", Now.AddMinutes(-30));
        chat.End(Now.AddMinutes(-20), "CustomerDisconnect", receivedAtUtc: Now.AddMinutes(-10));
        context.TicketInteractions.Add(chat);

        var old = _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddDays(-9));
        var oldCall = Call(old, WellKnownChannels.Phone, "v-old", Now.AddDays(-9));
        oldCall.End(Now.AddDays(-9).AddMinutes(5), "CustomerDisconnect", receivedAtUtc: Now.AddDays(-9).AddMinutes(5));
        context.TicketInteractions.Add(oldCall);

        var owned = _db.AddTicket(context, _db.CustomerServiceId, owner: Guid.NewGuid());
        var ownedCall = Call(owned, WellKnownChannels.Phone, "v-owned", Now.AddMinutes(-30));
        ownedCall.End(Now.AddMinutes(-20), "CustomerDisconnect", receivedAtUtc: Now.AddMinutes(-10));
        context.TicketInteractions.Add(ownedCall);

        // An old call whose answering agent was flagged (unmapped then): it must stay a candidate however old it is.
        var flagged = _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddDays(-9));
        var flaggedCall = Call(flagged, WellKnownChannels.Phone, "v-flagged", Now.AddDays(-9));
        flaggedCall.End(Now.AddDays(-9).AddMinutes(5), "CustomerDisconnect", receivedAtUtc: Now.AddDays(-9).AddMinutes(5));
        context.TicketInteractions.Add(flaggedCall);

        // A call that has not ended at all.
        var live = _db.AddTicket(context, _db.CustomerServiceId);
        context.TicketInteractions.Add(Call(live, WellKnownChannels.Phone, "v-live", Now.AddMinutes(-3)));
        context.SaveChanges();

        var connected = Now.AddDays(-9).AddMinutes(1);
        context.TicketWorkflowEvents.Add(new TicketWorkflowEvent(
            flagged.TicketId, WorkflowEventType.ManualAssignmentRequired, Now.AddDays(-9).AddMinutes(2), actorEmployeeId: null, ticketApprovalId: null,
            $"Answered by Genesys agent {flaggedGenesysId} — not mapped. Left in the Call Center queue for manual assignment. [GenesysUserId={flaggedGenesysId};Issue=NotMapped;ConnectedAtUtc={connected:O}]",
            Guid.NewGuid()));
        context.SaveChanges();
        return (phone.TicketId, digital.TicketId, old.TicketId, owned.TicketId, flagged.TicketId, live.TicketId);
    }

    [Fact]
    public async Task TheVoiceCandidateQuery_ReturnsOnlyRecentUnownedPhoneCalls_PlusAnyFlaggedAnswer_HoweverOld()
    {
        var fixedUser = Guid.NewGuid();
        var ids = Seed(fixedUser, "g-now-mapped");

        using var read = _db.CreateContext();
        var candidates = await new TicketUnassignedWorkRepository(read).ListVoiceCandidatesAsync(Now.AddHours(-24), 0, 100);

        Assert.Equal(new[] { ids.Phone, ids.Flagged }.Order(), candidates.Select(c => c.TicketId).Order());
        Assert.DoesNotContain(candidates, c => c.TicketId == ids.Digital);                    // never a digital ticket
        Assert.DoesNotContain(candidates, c => c.TicketId == ids.Old);                        // never a legacy backlog
        Assert.DoesNotContain(candidates, c => c.TicketId == ids.Owned);                      // never an owned ticket
        Assert.DoesNotContain(candidates, c => c.TicketId == ids.Live);                       // a call still in progress is not a candidate
    }

    [Fact]
    public async Task AnOldUnownedCall_KnownToNeedAPerson_IsACandidate_HoweverOld_SoATicketStuckOnTheAiAgentIsRecovered()
    {
        long stuck;
        long untouched;
        using (var context = _db.CreateContext())
        {
            context.Departments.Add(new Department("Call Center", "CC"));
            context.SaveChanges();

            var needsPerson = _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddDays(-9));
            var flaggedCall = Call(needsPerson, WellKnownChannels.Phone, "v-stuck", Now.AddDays(-9));
            flaggedCall.End(Now.AddDays(-9).AddMinutes(5), "CustomerDisconnect", receivedAtUtc: Now.AddDays(-9).AddMinutes(5));
            flaggedCall.MarkRequiresHumanFollowUp();
            context.TicketInteractions.Add(flaggedCall);

            var aiOnly = _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddDays(-9));
            var aiCall = Call(aiOnly, WellKnownChannels.Phone, "v-ai-only", Now.AddDays(-9));
            aiCall.End(Now.AddDays(-9).AddMinutes(5), "CustomerDisconnect", receivedAtUtc: Now.AddDays(-9).AddMinutes(5));
            context.TicketInteractions.Add(aiCall);
            context.SaveChanges();
            (stuck, untouched) = (needsPerson.TicketId, aiOnly.TicketId);
        }

        using var read = _db.CreateContext();
        var candidates = await new TicketUnassignedWorkRepository(read).ListVoiceCandidatesAsync(Now.AddHours(-24), 0, 100);

        Assert.Contains(candidates, c => c.TicketId == stuck && c.RequiresHumanFollowUp);
        Assert.DoesNotContain(candidates, c => c.TicketId == untouched);                      // an old AI-handled call nobody asked a person for is still never swept up
    }

    [Fact]
    public async Task TheEvidence_CarriesPhoneOnly_AndTheReceiptTimeOfTheEnd_NotTheSendersEventTime()
    {
        var ids = Seed(Guid.NewGuid(), "g-x");

        using var read = _db.CreateContext();
        var repo = new TicketUnassignedWorkRepository(read);
        var phone = await repo.GetEvidenceAsync(ids.Phone);
        var chat = await repo.GetEvidenceAsync(ids.Digital);
        var live = await repo.GetEvidenceAsync(ids.Live);

        Assert.True(phone!.GenesysIsPhoneOnly);
        Assert.Equal(Now.AddMinutes(-10), phone.VoiceEndReceivedAtUtc);                      // received, not the (earlier) reported end
        Assert.False(chat!.GenesysIsPhoneOnly);
        Assert.Null(chat.VoiceEndReceivedAtUtc);
        Assert.True(live!.GenesysIsPhoneOnly);
        Assert.Null(live.VoiceEndReceivedAtUtc);                                              // not ended: no window to measure
    }

    [Fact]
    public async Task TheLastOfferedAgent_IsReadFromTheOfferEvents_OrderedByOfferTime_AndResolvedAgainstTheCurrentMapping_NeverFromTheOwner()
    {
        var offeredUser = Guid.NewGuid();
        var ids = Seed(Guid.NewGuid(), "g-flag-unused");
        using (var context = _db.CreateContext())
        {
            context.Users.Add(new TigerCS.Infrastructure.Identity.ApplicationUser
            {
                Id = offeredUser, UserName = "offered@example.test", NormalizedUserName = "OFFERED@EXAMPLE.TEST", Email = "offered@example.test",
                SecurityStamp = Guid.NewGuid().ToString(), GenesysUserId = "g-offered-last"
            });
            // Three offers. The events ARRIVED in the order first / last / middle, but Genesys' own offer times say "g-offered-last" is the latest.
            void Offer(string id, DateTime offeredAt, DateTime receivedAt) => context.TicketWorkflowEvents.Add(new TicketWorkflowEvent(
                ids.Phone, WorkflowEventType.GenesysAgentOffered, receivedAt, actorEmployeeId: null, ticketApprovalId: null,
                $"Genesys offered the call to agent {id}. {OfferedAgent.Format(id, offeredAt, "HandoffAssignment")}", Guid.NewGuid()));
            Offer("g-offered-first", Now.AddMinutes(-29), Now.AddMinutes(-29));
            Offer("g-offered-last", Now.AddMinutes(-25), Now.AddMinutes(-28));
            Offer("g-offered-middle", Now.AddMinutes(-27), Now.AddMinutes(-27));
            context.SaveChanges();
        }

        using var read = _db.CreateContext();
        var repository = new TicketUnassignedWorkRepository(read);
        var evidence = await repository.GetEvidenceAsync(ids.Phone);

        Assert.Equal("g-offered-last", evidence!.VoiceLastOfferedGenesysUserId);               // the latest OFFER, whatever order the events arrived in
        Assert.Equal(offeredUser, evidence.VoiceLastOfferedEmployeeId);                        // resolved against the mapping that exists NOW
        Assert.Equal(Now.AddMinutes(-25), evidence.VoiceLastOfferedAtUtc);
        Assert.False(evidence.HumanAnswered);                                                  // an offer is never an answer
        Assert.Null(evidence.AnsweringEmployeeId);

        // A call nobody was ever offered carries no last offer; a digital ticket never carries one.
        Assert.Null((await repository.GetEvidenceAsync(ids.Old))!.VoiceLastOfferedGenesysUserId);
        Assert.Null((await repository.GetEvidenceAsync(ids.Digital))!.VoiceLastOfferedGenesysUserId);
    }

    [Fact]
    public async Task AFlaggedAnswer_IsResolvedAgainstTheCurrentMapping_SoACorrectedMappingIsNoticed()
    {
        var fixedUser = Guid.NewGuid();
        var ids = Seed(fixedUser, "g-now-mapped");
        using (var context = _db.CreateContext())
        {
            // The flag was written for an id that was unmapped at the time; a later flag for another id supersedes nothing about this ticket's latest one.
            context.TicketWorkflowEvents.Add(new TicketWorkflowEvent(
                ids.Flagged, WorkflowEventType.ManualAssignmentRequired, Now.AddDays(-8), actorEmployeeId: null, ticketApprovalId: null,
                "Answered by Genesys agent g-still-unmapped — not mapped. [GenesysUserId=g-still-unmapped;Issue=NotMapped]", Guid.NewGuid()));
            context.SaveChanges();
        }

        using var read = _db.CreateContext();
        var latestUnmapped = await new TicketUnassignedWorkRepository(read).GetEvidenceAsync(ids.Flagged);

        Assert.Equal("g-still-unmapped", latestUnmapped!.FlaggedAnsweringGenesysUserId);     // the LATEST flag wins
        Assert.Null(latestUnmapped.FlaggedAnsweringEmployeeId);                               // nobody maps to it yet
        Assert.True(latestUnmapped.HumanAnswered);

        using (var context = _db.CreateContext())
        {
            context.Database.ExecuteSqlInterpolated($"DELETE FROM TicketWorkflowEvents WHERE TicketId = {ids.Flagged} AND Note LIKE '%g-still-unmapped%'");
        }

        using var again = _db.CreateContext();
        var corrected = await new TicketUnassignedWorkRepository(again).GetEvidenceAsync(ids.Flagged);
        Assert.Equal("g-now-mapped", corrected!.FlaggedAnsweringGenesysUserId);
        Assert.Equal(fixedUser, corrected.FlaggedAnsweringEmployeeId);                        // the mapping that exists NOW
        Assert.Equal(Now.AddDays(-9).AddMinutes(1), corrected.FlaggedAnsweringConnectedAtUtc);   // the real connect time rode in the flag
        Assert.Equal(VoiceReconcileAction.AssignAnsweringAgent,
            VoiceCallClassifier.Classify(corrected, Now, new VoiceCallReconciliationOptions()).Action);
    }

    [Fact]
    public void TheFlagNote_RoundTrips_AndGarbageIsIgnored()
    {
        var parsed = FlaggedAnswer.TryParse("Answered by Genesys agent g-1 — x. [GenesysUserId=g-1;Issue=NotMapped;ConnectedAtUtc=2026-11-20T08:00:00.0000000Z]");

        Assert.Equal("g-1", parsed!.GenesysUserId);
        Assert.Equal("NotMapped", parsed.Issue);
        Assert.Equal(new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc), parsed.ConnectedAtUtc);
        Assert.Null(FlaggedAnswer.TryParse(null));
        Assert.Null(FlaggedAnswer.TryParse("no marker here"));
        Assert.Null(FlaggedAnswer.TryParse("[Issue=NotMapped]"));
        Assert.Null(FlaggedAnswer.TryParse("[GenesysUserId=;Issue=NotMapped]"));
    }


    // ---- offered / assigned / ringing is NOT an answer ----------------------------------------------------------------------------------

    private async Task<(long TicketId, UnassignedTicketEvidence Evidence)> OfferedCallAsync(string agentId, bool mappedUser)
    {
        long ticketId;
        using (var context = _db.CreateContext())
        {
            context.Departments.Add(new Department("Call Center", "CC"));
            if (mappedUser)
            {
                context.Users.Add(new TigerCS.Infrastructure.Identity.ApplicationUser
                {
                    Id = Guid.NewGuid(), UserName = "offered@example.test", NormalizedUserName = "OFFERED@EXAMPLE.TEST", Email = "offered@example.test",
                    SecurityStamp = Guid.NewGuid().ToString(), GenesysUserId = agentId
                });
            }

            context.SaveChanges();
            var ticket = _db.AddTicket(context, _db.CustomerServiceId);
            // The agent id Genesys named for a live transfer (RecordAgentIfAbsent): stored on the interaction, no handler, no connect event, no flag.
            var call = TicketInteraction.CreateFromGenesys(
                ticket.TicketId, WellKnownChannels.Phone, "+971500000000", "v-offered", calledNumber: null, genesysQueueId: null, genesysQueueName: null, genesysAgentId: agentId,
                genesysAgentName: null, interactionStartedAtUtc: Now.AddMinutes(-30), direction: "Inbound", createdAtUtc: Now.AddMinutes(-30), isOriginatingInteraction: true,
                genesysServiceRequestId: "svc1");
            call.End(Now.AddMinutes(-20), "CustomerDisconnect", receivedAtUtc: Now.AddMinutes(-10));
            context.TicketInteractions.Add(call);
            context.SaveChanges();
            ticketId = ticket.TicketId;
        }

        using var read = _db.CreateContext();
        return (ticketId, (await new TicketUnassignedWorkRepository(read).GetEvidenceAsync(ticketId))!);
    }

    [Theory]
    [InlineData("g-offered-unmapped", false)]    // a named agent who maps to nobody
    [InlineData("g-offered-mapped", true)]       // a mapped agent
    public async Task AnAgentIdAlone_OfferedAssignedOrRinging_IsNeverEvidenceOfAHumanAnswer_SoTheEndedCallIsDealtByRoundRobin(string agentId, bool mapped)
    {
        var (_, evidence) = await OfferedCallAsync(agentId, mapped);

        Assert.False(evidence.HumanAnswered);
        Assert.False(evidence.PhonePickupByUnmappedAgent);
        Assert.Null(evidence.UnmappedGenesysAgentId);
        Assert.Null(evidence.AnsweringEmployeeId);
        var result = VoiceCallClassifier.Classify(evidence, Now, new VoiceCallReconciliationOptions());
        Assert.Equal(new VoiceReconcileClassification(VoiceReconcileClass.NoAnswerConcluded, VoiceReconcileAction.ConcludeNoAnswer), result);
    }

    [Fact]
    public async Task AConnectedAgentWhoMapsToNobody_IsAHumanAnswer_FlaggedAndNeverRoundRobin()
    {
        var (ticketId, _) = await OfferedCallAsync("g-connected-unmapped", mappedUser: false);
        using (var context = _db.CreateContext())
        {
            context.TicketWorkflowEvents.Add(new TicketWorkflowEvent(
                ticketId, WorkflowEventType.ManualAssignmentRequired, Now.AddMinutes(-15), actorEmployeeId: null, ticketApprovalId: null,
                "Answered by Genesys agent g-connected-unmapped - not mapped. [GenesysUserId=g-connected-unmapped;Issue=NotMapped;ConnectedAtUtc=2026-09-10T11:40:00.0000000Z]", Guid.NewGuid()));
            context.SaveChanges();
        }

        using var read = _db.CreateContext();
        var evidence = (await new TicketUnassignedWorkRepository(read).GetEvidenceAsync(ticketId))!;

        Assert.True(evidence.HumanAnswered);
        Assert.True(evidence.PhonePickupByUnmappedAgent);
        Assert.Equal("g-connected-unmapped", evidence.UnmappedGenesysAgentId);
        Assert.Equal(VoiceReconcileClass.AnsweringAgentMappingPending, VoiceCallClassifier.Classify(evidence, Now, new VoiceCallReconciliationOptions()).Class);
    }

    [Theory]
    [InlineData(InteractionHandlerSource.Update, false)]            // a top-level agentId on an update: the agent may only have been offered / assigned
    [InlineData(InteractionHandlerSource.ConversationEnd, false)]   // ... or named on the end event
    [InlineData(InteractionHandlerSource.Routing, true)]            // the agent a routing update reported CONNECTED
    [InlineData(InteractionHandlerSource.Inquiry, true)]            // the pickup recorded when the call's ticket was created
    [InlineData(InteractionHandlerSource.Transcript, true)]         // a person who wrote to the customer
    public async Task AMappedHandlerCountsAsAnAnswer_OnlyWhenItsSourceIsAConfirmedConnection(InteractionHandlerSource source, bool answered)
    {
        var agentUser = Guid.NewGuid();
        var (ticketId, _) = await OfferedCallAsync("g-handler", mappedUser: false);
        using (var context = _db.CreateContext())
        {
            var interaction = context.TicketInteractions.Single(i => i.TicketId == ticketId);
            context.TicketInteractionHandlers.Add(TicketInteractionHandler.Create(
                interaction.TicketInteractionId, ticketId, agentUser, "g-handler", source, Now.AddMinutes(-25), Now.AddMinutes(-24)));
            context.SaveChanges();
        }

        using var read = _db.CreateContext();
        var evidence = (await new TicketUnassignedWorkRepository(read).GetEvidenceAsync(ticketId))!;

        Assert.Equal(answered, evidence.HumanAnswered);
        Assert.Equal(answered ? agentUser : (Guid?)null, evidence.AnsweringEmployeeId);
        Assert.Equal(
            answered ? VoiceReconcileClass.AnsweringAgentResolvable : VoiceReconcileClass.NoAnswerConcluded,
            VoiceCallClassifier.Classify(evidence, Now, new VoiceCallReconciliationOptions()).Class);
    }
}
