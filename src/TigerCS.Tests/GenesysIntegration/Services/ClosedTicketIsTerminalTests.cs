using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Services;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// <b>Closed is terminal for every automated workflow.</b> Whoever closed the ticket (a person, the AI, the inactivity job, the AI-only timeout), a
/// late, duplicate, replayed or out-of-order Genesys event — a connected agent, a human transcript line, a handoff, the human-follow-up flag, a
/// conversation end — is recorded as evidence and changes nothing about the ticket: no reopen, no status, owner, department, SLA, closure or
/// notification. A new conversation still gets its own ticket; a replay of the old one never does. The only way back is the explicit Reopen action.
/// </summary>
public class ClosedTicketIsTerminalTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    private sealed record Snapshot(
        TicketStatus Status, Guid? Owner, int Department, byte? Outcome, SlaState Sla, DateTime? FirstResponse, int Reopens,
        int Assignments, int WorkflowEvents, int StatusHistory, int HistoryEntries, int Outbox);

    private static Snapshot Capture(GenesysServiceFixture f, Ticket t) => new(
        t.TicketStatus, t.CurrentOwnerEmployeeId, t.CurrentDepartmentId, t.ResolutionOutcome, t.SlaState, t.FirstHumanResponseAtUtc, t.ReopenCount,
        f.TicketAssignments.Added.Count(a => a.TicketId == t.TicketId), f.WorkflowEvents.All.Count(e => e.TicketId == t.TicketId),
        f.StatusHistory.Added.Count(h => h.TicketId == t.TicketId), f.AssignmentHistory.Entries.Count(e => e.TicketId == t.TicketId),
        f.Outbox.Committed.Count + f.Outbox.Staged.Count);

    public enum Closer { Person, Ai, Inactivity, AiOnlyTimeout }

    private sealed record Closed(GenesysServiceFixture F, Ticket Ticket, string ConversationId, string AgentGenesysId, Guid AgentId);

    private static async Task<Closed> ClosedTicketAsync(Closer closer, GenesysChannel channel = GenesysChannel.WhatsApp)
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var agentGenesysId = "g-late-agent";
        var agentId = f.AddAgent(agentGenesysId, "Late Agent");
        const string conv = "conv-closed";
        if (closer == Closer.Inactivity)
        {
            channel = GenesysChannel.WebsiteChat;   // only a website/chatbot conversation has the customer-inactivity clock
        }

        var ingested = await f.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(conv, channel, CustomerPhone: "+971500000001", DepartmentId: cs.DepartmentId));
        var ticket = f.Tickets.All.Single(t => t.TicketId == ingested.Ticket!.TicketId);

        switch (closer)
        {
            case Closer.Person:
                ticket.AssignTo(Guid.NewGuid());
                ticket.ChangeStatus(TicketStatus.InProgress);
                ticket.Resolve(ResolutionOutcome.Resolved, null);
                ticket.Close();
                break;

            case Closer.Ai:
                var update = await f.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(conv, ClosedByAi: true));
                Assert.True(update.ClosedByAi, update.ClosedByAiNote);
                break;

            case Closer.Inactivity:
                await f.TicketUpdate.UpdateAsync(ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(conv, AwaitingCustomerReply: true));
                f.Clock.Advance(TimeSpan.FromMinutes(11));
                foreach (var id in await f.InactivityClose.ListDueAsync())
                {
                    await f.InactivityClose.TryCloseAsync(id);
                }

                break;

            case Closer.AiOnlyTimeout:
                f.Clock.Advance(TimeSpan.FromHours(18) + TimeSpan.FromMinutes(1));
                foreach (var id in await f.AiOnlyClose.ListCandidatesAsync(afterTicketId: 0))
                {
                    await f.AiOnlyClose.TryCloseAsync(id);
                }

                break;
        }

        Assert.Equal(TicketStatus.Closed, ticket.TicketStatus);
        return new Closed(f, ticket, conv, agentGenesysId, agentId);
    }

    /// <summary>Every kind of late event that used to be able to move a ticket: connect, human line, handoff, flag, end.</summary>
    private static async Task DeliverLateEventsAsync(Closed c)
    {
        var late = c.F.Clock.GetUtcNow().UtcDateTime.AddMinutes(-3);
        var human = new GenesysTranscriptMessageDto("HumanAgent", late, "Hello, how can I help?", "Late Agent", c.AgentGenesysId, "late-1");
        var ai = new GenesysTranscriptMessageDto("VirtualAgent", late.AddMinutes(-1), "Hi, I am the assistant", "Bot", ExternalMessageId: "late-0");

        await c.F.TicketUpdate.UpdateAsync(ServiceAccount, c.Ticket.TicketId,
            new GenesysTicketUpdateDto(c.ConversationId, Routing: new GenesysRoutingUpdateDto(AgentId: c.AgentGenesysId, ConnectedAtUtc: late)));
        await c.F.TicketUpdate.UpdateAsync(ServiceAccount, c.Ticket.TicketId,
            new GenesysTicketUpdateDto(c.ConversationId, Transcript: [ai, human]));
        await c.F.TicketUpdate.UpdateAsync(ServiceAccount, c.Ticket.TicketId,
            new GenesysTicketUpdateDto(c.ConversationId, HumanFollowUpRequired: true));
        await c.F.TicketUpdate.UpdateAsync(ServiceAccount, c.Ticket.TicketId,
            new GenesysTicketUpdateDto(c.ConversationId, Handoff: new GenesysHandoffUpdateDto(Required: true, Reason: "late", Trigger: "AiEscalated")));
        await c.F.TicketUpdate.UpdateAsync(ServiceAccount, c.Ticket.TicketId,
            new GenesysTicketUpdateDto(c.ConversationId, Ended: new GenesysConversationEndUpdateDto(late.AddMinutes(1), "Disconnect", [ai, human])));
    }

    [Theory]
    [InlineData(Closer.Person)]
    [InlineData(Closer.Ai)]
    [InlineData(Closer.Inactivity)]
    [InlineData(Closer.AiOnlyTimeout)]
    public async Task LateGenesysEvents_NeverChangeAClosedTicket_WhoeverClosedIt(Closer closer)
    {
        var c = await ClosedTicketAsync(closer);
        var before = Capture(c.F, c.Ticket);

        await DeliverLateEventsAsync(c);
        await DeliverLateEventsAsync(c);   // a replay of the same events changes nothing either

        var after = Capture(c.F, c.Ticket);
        // The only things that may differ are evidence rows: nothing about the ticket, its ownership history or its notifications.
        Assert.Equal(before, after);
        Assert.Equal(TicketStatus.Closed, c.Ticket.TicketStatus);
        Assert.Null(c.Ticket.CurrentOwnerEmployeeId);
        Assert.DoesNotContain(c.F.WorkflowEvents.All, e => e.TicketId == c.Ticket.TicketId && e.EventType == WorkflowEventType.Reopened);
        Assert.DoesNotContain(c.F.StatusHistory.Added, h => h.TicketId == c.Ticket.TicketId && h.OldValue == (byte)TicketStatus.Closed);
    }

    [Theory]
    [InlineData(Closer.Person)]
    [InlineData(Closer.Ai)]
    public async Task LateHumanEvidence_IsRecordedAsAuditEvidence_WithoutTouchingTheTicket(Closer closer)
    {
        var c = await ClosedTicketAsync(closer);

        await DeliverLateEventsAsync(c);

        Assert.Contains(c.F.Audit.Entries, e => e.Action == GenesysAuditActions.EventIgnoredOnClosedTicket
            && e.AfterValue!.Contains("TicketChanged=false", StringComparison.Ordinal));
        Assert.DoesNotContain(c.F.Audit.Entries, e => e.Action is "AssignOnAnswer" or "Transfer" or "AnswerAssignmentSkipped");
    }

    [Fact]
    public async Task AHandoffRequestForAClosedTicketsConversation_IsRefused_AndLeavesOnlyAuditEvidence()
    {
        var c = await ClosedTicketAsync(Closer.Ai);
        var before = Capture(c.F, c.Ticket);

        var result = await c.F.TicketUpdate.UpdateAsync(ServiceAccount, c.Ticket.TicketId,
            new GenesysTicketUpdateDto(c.ConversationId, Handoff: new GenesysHandoffUpdateDto(Required: true, Reason: "late")));

        Assert.Equal(GenesysTicketUpdateOutcome.TicketClosed, result.Outcome);
        Assert.Equal(before, Capture(c.F, c.Ticket));
        Assert.Contains(c.F.Audit.Entries, e => e.Action == GenesysAuditActions.EventIgnoredOnClosedTicket);
    }

    [Fact]
    public async Task ReplayedCreate_ForAClosedTicketsConversation_ReturnsTheSameTicket_AndCreatesNothing()
    {
        var c = await ClosedTicketAsync(Closer.Person);
        var tickets = c.F.Tickets.All.Count;

        var replay = await c.F.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto(c.ConversationId, GenesysChannel.WhatsApp, CustomerPhone: "+971500000001"));

        Assert.Equal(GenesysIngestionOutcome.AlreadyIngested, replay.Outcome);
        Assert.Equal(c.Ticket.TicketId, replay.Ticket!.TicketId);
        Assert.Equal(tickets, c.F.Tickets.All.Count);
        Assert.Equal(TicketStatus.Closed, c.Ticket.TicketStatus);
    }

    [Fact]
    public async Task AGenuinelyNewConversation_StillGetsItsOwnTicket_AndTheClosedOneStaysClosed()
    {
        var c = await ClosedTicketAsync(Closer.Ai);
        
        var fresh = await c.F.Ingestion.IngestAsync(
            ServiceAccount, new GenesysInquiryDto("conv-brand-new", GenesysChannel.WhatsApp, CustomerPhone: "+971500000001", DepartmentId: c.Ticket.OriginatingDepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, fresh.Outcome);
        Assert.NotEqual(c.Ticket.TicketId, fresh.Ticket!.TicketId);
        Assert.Equal(TicketStatus.Closed, c.Ticket.TicketStatus);
    }

    [Fact]
    public async Task ALateAiAssignmentEvent_IsNotRecordedInTheOwnershipHistoryOfAClosedTicket()
    {
        var c = await ClosedTicketAsync(Closer.Person);
        var before = c.F.AssignmentHistory.Entries.Count;

        var recorded = await c.F.AutoAssignment.RecordAiAgentAssignmentAsync(
            c.Ticket, Guid.NewGuid(), c.ConversationId, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);

        Assert.False(recorded);
        Assert.Equal(before, c.F.AssignmentHistory.Entries.Count);
    }

    [Fact]
    public async Task ClosedTicket_CannotBeReopenedByAnyDomainMutator_ExceptTheExplicitReopen()
    {
        var c = await ClosedTicketAsync(Closer.Person);

        Assert.Throws<TicketClosedException>(() => c.Ticket.ChangeStatus(TicketStatus.Open));
        Assert.Throws<TicketClosedException>(() => c.Ticket.ChangeStatus(TicketStatus.InProgress));
        Assert.Throws<TicketClosedException>(() => c.Ticket.AssignTo(c.AgentId));
        Assert.Throws<TicketClosedException>(() => c.Ticket.TransferToDepartment(c.F.CallCenter!.DepartmentId));
        Assert.Throws<TicketClosedException>(() => c.Ticket.ReleaseOwner());
        Assert.Throws<TicketClosedException>(() => c.Ticket.RecordFirstHumanResponse(DateTime.UtcNow));
        Assert.Equal(TicketStatus.Closed, c.Ticket.TicketStatus);
    }

    /// <summary>
    /// Architecture guard: <c>Ticket.Reopen</c> is the only transition out of Closed, and its single production caller is the explicit, authorized
    /// <c>TicketLifecycleAppService.ReopenAsync</c>. A new caller anywhere (a job, a Genesys handler, a matcher) fails this test and needs a review.
    /// </summary>
    [Fact]
    public void TicketReopen_HasExactlyOneProductionCaller_TheExplicitReopenAction()
    {
        var root = FindSourceRoot();
        var callers = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !p.Contains("TigerCS.Tests"))
            .Where(p => File.ReadAllText(p).Contains("ticket.Reopen(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(["TicketLifecycleAppService.cs"], callers);
    }

    /// <summary>
    /// Nothing may write a ticket's status behind the domain's back: the setter is private (only <c>Ticket</c> itself assigns it, and every such method
    /// refuses a Closed ticket except the explicit Reopen), and no set-based update, raw SQL or repository operation in production code touches it.
    /// </summary>
    [Fact]
    public void NoProductionCode_WritesTicketStatus_ExceptTheTicketAggregateItself()
    {
        var setter = typeof(Ticket).GetProperty(nameof(Ticket.TicketStatus))!.SetMethod!;
        Assert.True(setter.IsPrivate);

        var root = FindSourceRoot();
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !p.Contains("TigerCS.Tests")
                        && !p.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Where(p =>
            {
                var text = File.ReadAllText(p);
                return text.Contains("SetProperty(t => t.TicketStatus", StringComparison.Ordinal)
                    || text.Contains("SetProperty(x => x.TicketStatus", StringComparison.Ordinal)
                    || System.Text.RegularExpressions.Regex.IsMatch(text, @"UPDATE\s+\[?(dbo\.)?\[?Tickets\]?\s+SET", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            })
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>Every domain mutator that could move a Closed ticket refuses it; <c>Reopen</c> is the only way out, and it needs an explicit target department.</summary>
    [Fact]
    public async Task EveryTicketMutator_RefusesAClosedTicket_ExceptTheExplicitReopen()
    {
        var c = await ClosedTicketAsync(Closer.Ai);

        Assert.Throws<TicketClosedException>(() => c.Ticket.AssignToAiAgent(Guid.NewGuid()));
        Assert.Throws<TicketClosedException>(() => c.Ticket.ReturnToQueue());
        Assert.Throws<TicketClosedException>(() => c.Ticket.CloseForCustomerInactivity());
        Assert.Throws<TicketClosedException>(() => c.Ticket.CloseForAiCompletion());
        Assert.Throws<TicketClosedException>(() => c.Ticket.RaiseEscalationLevel(EscalationLevel.Level2));
        Assert.Equal(TicketStatus.Closed, c.Ticket.TicketStatus);

        c.Ticket.Reopen(c.Ticket.OriginatingDepartmentId);                       // the one explicit way back
        Assert.NotEqual(TicketStatus.Closed, c.Ticket.TicketStatus);
        Assert.Equal(1, c.Ticket.ReopenCount);
    }

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "TigerCS.Application")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the src folder.");
    }
}
