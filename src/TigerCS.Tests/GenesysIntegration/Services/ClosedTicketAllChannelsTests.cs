using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// <b>Closed is terminal on EVERY channel</b> (voice and digital), whoever closed it: nothing automatic - a late connect, a human transcript line, a handoff, a human-follow-up
/// flag, the conversation end, a repeated AI closure, a voice reconciliation run - reopens it, changes its owner, department, SLA or closure, and a continued conversation never
/// reopens it. The historical owner stays visible (the current-owner field is intentionally cleared at closure), for AI closure and for human closure.
/// </summary>
public class ClosedTicketAllChannelsTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    public enum Closer { Ai, Person }

    public static TheoryData<GenesysChannel, Closer> Matrix
    {
        get
        {
            var data = new TheoryData<GenesysChannel, Closer>();
            foreach (var channel in new[] { GenesysChannel.Phone, GenesysChannel.WebsiteChat, GenesysChannel.WhatsApp, GenesysChannel.Instagram })
            {
                data.Add(channel, Closer.Ai);
                data.Add(channel, Closer.Person);
            }

            return data;
        }
    }

    private sealed record Snapshot(
        TicketStatus Status, Guid? Owner, Guid? AiOwner, int Department, byte? Outcome, DateTime? FirstResponse, int Reopens,
        int Assignments, int AssignmentHistory, int Resolutions, int StatusHistory, int Handoffs, byte? Priority, int? RequestType);

    private static Snapshot Capture(GenesysServiceFixture f, Ticket t) => new(
        t.TicketStatus, t.CurrentOwnerEmployeeId, t.AiAgentOwnerEmployeeId, t.CurrentDepartmentId, t.ResolutionOutcome, t.FirstHumanResponseAtUtc, t.ReopenCount,
        f.TicketAssignments.Added.Count(a => a.TicketId == t.TicketId), f.AssignmentHistory.Entries.Count(e => e.TicketId == t.TicketId),
        f.Resolutions.Added.Count(r => r.TicketId == t.TicketId), f.StatusHistory.Added.Count(h => h.TicketId == t.TicketId),
        f.Handoffs.All.Count(h => h.TicketId == t.TicketId), t.PriorityId, t.RequestTypeId);

    private sealed record Closed(GenesysServiceFixture F, Ticket Ticket, string ConversationId, Guid CloserOwner, string LateAgentGenesysId, Guid LateAgent);

    private static async Task<Closed> ClosedTicketAsync(GenesysChannel channel, Closer closer)
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var owner = f.AddAgent("g-owner", "Owner Agent");
        var late = f.AddAgent("g-late", "Late Agent");
        const string conv = "conv-closed-all";

        var ingested = await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conv, channel, CustomerPhone: "+971500000001", DepartmentId: cs.DepartmentId,
                AgentId: closer == Closer.Person && channel == GenesysChannel.Phone ? "g-owner" : null));
        var ticket = f.Tickets.All.Single(t => t.TicketId == ingested.Ticket!.TicketId);

        if (closer == Closer.Ai)
        {
            var update = await f.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(conv, ClosedByAi: true));
            Assert.True(update.ClosedByAi, update.ClosedByAiNote);
        }
        else
        {
            if (ticket.CurrentOwnerEmployeeId is null)
            {
                ticket.AssignTo(owner);
                await f.TicketAssignments.AddAsync(new TicketAssignment(ticket.TicketId, owner, ticket.CurrentDepartmentId, DateTime.UtcNow, assigningActorEmployeeId: owner));
            }

            ticket.ChangeStatus(TicketStatus.InProgress);
            ticket.Resolve(ResolutionOutcome.Resolved, null);
            ticket.Close();
        }

        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        return new Closed(f, ticket, conv, owner, "g-late", late);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task AClosedTicket_IsNeverReopened_ReOwned_OrChanged_ByAnyLateEvent_OnAnyChannel(GenesysChannel channel, Closer closer)
    {
        var c = await ClosedTicketAsync(channel, closer);
        var before = Capture(c.F, c.Ticket);
        var late = DateTime.UtcNow.AddMinutes(-3);
        var human = new GenesysTranscriptMessageDto("HumanAgent", late, "Hello, how can I help?", "Late Agent", c.LateAgentGenesysId, "late-1");

        await c.F.TicketUpdate.UpdateAsync(ServiceAccount, c.Ticket.TicketId,
            new GenesysTicketUpdateDto(c.ConversationId, Routing: new GenesysRoutingUpdateDto(AgentId: c.LateAgentGenesysId, ConnectedAtUtc: late)));
        await c.F.TicketUpdate.UpdateAsync(ServiceAccount, c.Ticket.TicketId, new GenesysTicketUpdateDto(c.ConversationId, Transcript: [human]));
        await c.F.TicketUpdate.UpdateAsync(ServiceAccount, c.Ticket.TicketId, new GenesysTicketUpdateDto(c.ConversationId, HumanFollowUpRequired: true));
        await c.F.TicketUpdate.UpdateAsync(ServiceAccount, c.Ticket.TicketId,
            new GenesysTicketUpdateDto(c.ConversationId, Handoff: new GenesysHandoffUpdateDto(Required: true, Reason: "late", Trigger: "AiEscalated")));
        await c.F.TicketUpdate.UpdateAsync(ServiceAccount, c.Ticket.TicketId,
            new GenesysTicketUpdateDto(c.ConversationId, Ended: new GenesysConversationEndUpdateDto(late.AddMinutes(1), "Disconnect", [human])));
        await c.F.TicketUpdate.UpdateAsync(ServiceAccount, c.Ticket.TicketId, new GenesysTicketUpdateDto(c.ConversationId, ClosedByAi: true));
        await c.F.AgentHandoff.RequestAsync(ServiceAccount, new GenesysHandoffRequestDto(c.ConversationId, AgentAvailable: true, Mode: "HumanTakeover", AgentId: c.LateAgentGenesysId));

        Assert.Equal(before, Capture(c.F, c.Ticket));                                          // not reopened, not re-owned, not moved, SLA and closure intact
        Assert.Equal(TicketStatus.Closed, c.Ticket.TicketStatus);
        Assert.Null(c.Ticket.CurrentOwnerEmployeeId);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task TheVoiceReconciler_NeverTouchesAClosedTicket_OnAnyChannel(GenesysChannel channel, Closer closer)
    {
        var c = await ClosedTicketAsync(channel, closer);
        var before = Capture(c.F, c.Ticket);
        c.F.Clock.Advance(TimeSpan.FromHours(1));
        var evidence = new ClosedEvidence(c.Ticket, channel == GenesysChannel.Phone);
        var reconciler = new VoiceCallReconciler(evidence, c.F.Tickets, c.F.Interactions, c.F.AutoAssignment, c.F.AnsweringAgents, c.F.UnitOfWork, c.F.Audit, c.F.Clock);

        var result = await reconciler.TryReconcileAsync(c.Ticket.TicketId, new VoiceCallReconciliationOptions(), dryRun: false);

        Assert.Equal(VoiceReconcileOutcome.Skipped, result.Outcome);
        Assert.Equal(before, Capture(c.F, c.Ticket));
    }

    /// <summary>Evidence for a closed ticket (the repository reports a Closed ticket's status faithfully): the classifier must refuse it.</summary>
    private sealed class ClosedEvidence(Ticket ticket, bool phone) : ITicketUnassignedWorkRepository
    {
        public Task<UnassignedTicketEvidence?> GetEvidenceAsync(long ticketId, CancellationToken cancellationToken = default) =>
            Task.FromResult<UnassignedTicketEvidence?>(new UnassignedTicketEvidence(
                ticket.TicketId, ticket.TicketNumber, ticket.TicketStatus, ticket.CurrentOwnerEmployeeId, ticket.CurrentDepartmentId, ticket.OriginatingDepartmentId, ticket.CreatedAtUtc,
                false, true, false, true, false, false, null, null, false, null, ticket.CreatedAtUtc, false, false,
                GenesysIsPhoneOnly: phone, VoiceEndReceivedAtUtc: ticket.CreatedAtUtc.AddMinutes(1)));

        public Task<IReadOnlyList<UnassignedTicketEvidence>> ListOpenUnownedAsync(long afterTicketId, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UnassignedTicketEvidence>>([]);
        public Task<IReadOnlyList<UnassignedTicketEvidence>> ListActiveUnownedAsync(long afterTicketId, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UnassignedTicketEvidence>>([]);
    }

    [Theory]
    [InlineData(GenesysChannel.Phone)]
    [InlineData(GenesysChannel.WebsiteChat)]
    [InlineData(GenesysChannel.WhatsApp)]
    public async Task ContinuingTheSameConversation_AfterClosure_ReturnsTheClosedTicket_AndNeverReopensIt(GenesysChannel channel)
    {
        var c = await ClosedTicketAsync(channel, Closer.Ai);
        var before = Capture(c.F, c.Ticket);
        var ticketsBefore = c.F.Tickets.All.Count;

        var again = await c.F.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(c.ConversationId, channel, CustomerPhone: "+971500000001", DepartmentId: c.Ticket.OriginatingDepartmentId, HumanFollowUpRequired: true));

        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, again.Outcome);
        Assert.Equal(ticketsBefore, c.F.Tickets.All.Count);                                    // no second ticket either
        Assert.Equal(before, Capture(c.F, c.Ticket));
    }

    // ---- historical owner is displayed and reported ------------------------------------------------------------------------------

    private static TicketQueryAppService Query(Closed c, FakeEmployeeRepository employees) =>
        new(c.F.Tickets, c.F.DepartmentAssignments, c.F.Resolutions, c.F.StatusHistory, ReopenPolicy.Default, TimeProvider.System,
            handlerRepository: FakeTicketInteractionHandlerRepository.Permissive(), employeeRepository: employees, ticketAssignmentRepository: c.F.TicketAssignments);

    private static async Task<(TicketAssignmentView Detail, Guid Manager)> DetailAsync(Closed c, params (Guid Id, string Name)[] people)
    {
        var employees = new FakeEmployeeRepository();
        foreach (var (id, name) in people)
        {
            employees.Add(new Employee(id, name, isGeynessStaff: true, DateTime.UtcNow));
        }

        var manager = Guid.NewGuid();
        var result = await Query(c, employees).GetDetailAsync(manager, [Roles.CsManager], c.Ticket.TicketId);
        Assert.Equal(TicketQueryOutcome.Success, result.Outcome);
        return (new TicketAssignmentView(result.Response!.CurrentOwnerEmployeeId, result.Response.LastResponsibleEmployeeId, result.Response.LastResponsibleName, result.Response.TicketStatus), manager);
    }

    private sealed record TicketAssignmentView(Guid? Current, Guid? LastResponsible, string? LastResponsibleName, string Status);

    [Theory]
    [InlineData(GenesysChannel.Phone)]
    [InlineData(GenesysChannel.WebsiteChat)]
    public async Task AiClosure_TheClosedTicketStillNamesTheAiAgent_AsItsLastResponsible_WithNoCurrentOwner(GenesysChannel channel)
    {
        var c = await ClosedTicketAsync(channel, Closer.Ai);
        var aiAgent = c.Ticket.AiAgentOwnerEmployeeId!.Value;

        var (view, _) = await DetailAsync(c, (aiAgent, "AI Agent"));

        Assert.Equal("Closed", view.Status);
        Assert.Null(view.Current);
        Assert.Equal(aiAgent, view.LastResponsible);
        Assert.Equal("AI Agent", view.LastResponsibleName);
        Assert.Equal(aiAgent, Assert.Single(c.F.Resolutions.Added, r => r.TicketId == c.Ticket.TicketId).ResolvingEmployeeId);   // ClosedBy = the AI Agent
    }

    [Theory]
    [InlineData(GenesysChannel.Phone)]
    [InlineData(GenesysChannel.WhatsApp)]
    public async Task HumanClosure_TheClosedTicketStillNamesTheLastHumanAssignee_AndTheAssignmentRowsSurvive(GenesysChannel channel)
    {
        var c = await ClosedTicketAsync(channel, Closer.Person);
        var assignmentsBefore = c.F.TicketAssignments.Added.Count(a => a.TicketId == c.Ticket.TicketId);

        var (view, _) = await DetailAsync(c, (c.CloserOwner, "Owner Agent"));

        Assert.Null(view.Current);                                                              // the current-owner field is intentionally cleared at closure ...
        Assert.Equal(c.CloserOwner, view.LastResponsible);                                      // ... the historical owner is still displayed
        Assert.Equal("Owner Agent", view.LastResponsibleName);
        Assert.True(assignmentsBefore >= 1);                                                    // and the assignment history was never deleted
    }

    [Fact]
    public async Task AnOpenTicket_ShowsItsCurrentOwner_AndNoLastResponsible()
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var agent = f.AddAgent("g-a", "Agent A");
        var ingested = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto("conv-open", GenesysChannel.Phone, CustomerPhone: "+971500000001", DepartmentId: cs.DepartmentId, AgentId: "g-a"));
        var ticket = f.Tickets.All.Single(t => t.TicketId == ingested.Ticket!.TicketId);
        var closed = new Closed(f, ticket, "conv-open", agent, "g-x", Guid.NewGuid());

        var (view, _) = await DetailAsync(closed, (agent, "Agent A"));

        Assert.Equal(agent, view.Current);
        Assert.Null(view.LastResponsible);
    }
}
