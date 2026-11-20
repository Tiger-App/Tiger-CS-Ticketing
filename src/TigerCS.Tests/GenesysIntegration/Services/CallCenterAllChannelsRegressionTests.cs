using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Regression for TG-LCS-20261102-0015 (Open, "Leasing Customer Services Queue (Unassigned)" instead of Call Center + one agent).
///
/// Root cause covered here: Genesys opens the ticket first and only LATER reports <c>humanFollowUpRequired: true</c>. That update
/// recorded the flag but never routed the ticket, so it stayed unassigned in its originating department. Every Genesys channel
/// (Phone, Website chat, WhatsApp, Social media) must reach the Call Center QUEUE — at creation when the flag is already known, and on the
/// update otherwise. The AI Agent stays the owner and NO person is dealt the ticket (no Round Robin, rotation untouched) until somebody actually
/// accepts it; human ownership is never overwritten and an AI-only conversation is never touched.
/// </summary>
public class CallCenterAllChannelsRegressionTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    public static TheoryData<GenesysChannel> Channels => new()
    {
        GenesysChannel.Phone, GenesysChannel.WebsiteChat, GenesysChannel.WhatsApp, GenesysChannel.SocialMedia,
        GenesysChannel.Instagram, GenesysChannel.Facebook
    };

    private sealed record Scenario(GenesysServiceFixture F, Department Leasing, Guid[] Agents);

    private static Scenario Build(int agentCount = 2)
    {
        var f = new GenesysServiceFixture(callCenterFirst: true);
        var leasing = f.Departments.AddDepartment("Leasing Customer Services", "LCS");
        var agents = Enumerable.Range(0, agentCount).Select(_ => Guid.NewGuid()).OrderBy(id => id).ToArray();
        foreach (var id in agents)
        {
            f.DepartmentAssignments.Assignments.Add(
                new UserDepartmentAssignment(id, f.CallCenter!.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        }

        // A leasing employee must never receive a Call Center ticket.
        f.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(Guid.NewGuid(), leasing.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        return new Scenario(f, leasing, agents);
    }

    private static async Task<Ticket> IngestAsync(Scenario s, string conversationId, GenesysChannel channel, bool? humanFollowUp)
    {
        var result = await s.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(
                conversationId, channel, CustomerPhone: "+971500000001", DepartmentId: s.Leasing.DepartmentId,
                HumanFollowUpRequired: humanFollowUp));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return s.F.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId);
    }

    /// <summary>Waiting in the Call Center queue for a person: department moved (originating untouched), still the AI's, nobody dealt, no assignment row.</summary>
    private static void AssertQueuedForAPerson(Scenario s, Ticket ticket)
    {
        Assert.Equal(s.F.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(s.Leasing.DepartmentId, ticket.OriginatingDepartmentId);                 // preserved
        Assert.Null(ticket.CurrentOwnerEmployeeId);                                           // no human owner
        Assert.Equal(ServiceAccount, ticket.AiAgentOwnerEmployeeId);                          // the AI Agent is still the owner
        Assert.DoesNotContain(s.F.TicketAssignments.Added, a => a.TicketId == ticket.TicketId);
        Assert.DoesNotContain(s.F.Audit.Entries, e => e.Action == "AutoAssign" && e.EntityId == ticket.TicketId.ToString());
    }

    private static bool RotationUntouched(Scenario s) => s.F.Rotation.Rows.Values.All(r => r.LastAssignedEmployeeId is null);

    private static Task<GenesysTicketUpdateResult> UpdateAsync(Scenario s, Ticket ticket, string conversationId, bool? humanFollowUp) =>
        s.F.TicketUpdate.UpdateAsync(
            ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(conversationId, HumanFollowUpRequired: humanFollowUp));

    [Fact]
    public async Task EveryChannel_QueuesInTheCallCenter_NobodyIsDealt_TheRotationNeverAdvances_AndAiHandledTicketsStayWithTheAi()
    {
        var s = Build(agentCount: 3);
        var channels = new[]
        {
            GenesysChannel.Phone, GenesysChannel.WebsiteChat, GenesysChannel.WhatsApp, GenesysChannel.SocialMedia,
            GenesysChannel.Instagram, GenesysChannel.Facebook, GenesysChannel.Phone
        };

        for (var i = 0; i < channels.Length; i++)
        {
            var ticket = await IngestAsync(s, $"c-seq-{i}", channels[i], humanFollowUp: true);
            AssertQueuedForAPerson(s, ticket);                                   // every human-needed ticket waits in the queue, whatever the channel
        }

        Assert.True(RotationUntouched(s));                                       // Round Robin is not used for AI-handled tickets: the cursor never moved
        Assert.Empty(s.F.TicketAssignments.Added);

        // An AI-only conversation (no human requested) stays exactly where it is with the AI and is not an assignment failure.
        var ai = await IngestAsync(s, "c-ai", GenesysChannel.WebsiteChat, humanFollowUp: false);
        Assert.Null(ai.CurrentOwnerEmployeeId);
        Assert.Equal(ServiceAccount, ai.AiAgentOwnerEmployeeId);
        Assert.Equal(s.Leasing.DepartmentId, ai.CurrentDepartmentId);
        Assert.DoesNotContain(s.F.WorkflowEvents.All, e => e.TicketId == ai.TicketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);
        Assert.True(RotationUntouched(s));
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task HumanFollowUpKnownAtCreation_EntersTheCallCenterQueue_StillOwnedByTheAi_WithNoRoundRobinAgent(GenesysChannel channel)
    {
        var s = Build();

        var t1 = await IngestAsync(s, "c-1", channel, humanFollowUp: true);
        var t2 = await IngestAsync(s, "c-2", channel, humanFollowUp: true);

        Assert.All(new[] { t1, t2 }, t => AssertQueuedForAPerson(s, t));
        Assert.True(RotationUntouched(s));
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task HumanFollowUpReportedAfterCreation_RoutesTheTicketToTheCallCenterQueue_WithNoAgentDealt(GenesysChannel channel)
    {
        var s = Build();

        // Exactly the production symptom: created in Leasing, Open, nobody owns it.
        var ticket = await IngestAsync(s, "c-late", channel, humanFollowUp: null);
        Assert.Equal(s.Leasing.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        var statusBefore = ticket.TicketStatus;

        var update = await UpdateAsync(s, ticket, "c-late", humanFollowUp: true);

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, update.Outcome);
        AssertQueuedForAPerson(s, ticket);
        Assert.True(RotationUntouched(s));                                       // no Round Robin turn
        Assert.Equal(statusBefore, ticket.TicketStatus);                         // status untouched
        Assert.Contains(s.F.Audit.Entries, e => e.Action == "Transfer" && e.AfterValue!.Contains("routed to Call Center first"));
        var queued = s.F.AssignmentHistory.Entries.Where(h => h.TicketId == ticket.TicketId).OrderBy(h => h.ChangedAtUtc).ThenBy(h => h.TicketAssignmentHistoryEntryId).Last();
        Assert.Equal(TicketAssignmentSource.GenesysQueueRouting, queued.Source);
        Assert.Equal(s.Leasing.DepartmentId, queued.PreviousDepartmentId);
        Assert.Equal(s.F.CallCenter!.DepartmentId, queued.DepartmentId);
        Assert.Equal(ServiceAccount, queued.PreviousEmployeeId);                 // the AI Agent stays the owner: AI Agent -> AI Agent, only the department moved
        Assert.Equal(ServiceAccount, queued.NewEmployeeId);
    }

    [Fact]
    public async Task RepeatedHumanFollowUpUpdate_IsIdempotent_NoSecondMoveNoAssignmentAndTheRotationNeverAdvances()
    {
        var s = Build();
        var ticket = await IngestAsync(s, "c-idem", GenesysChannel.WebsiteChat, humanFollowUp: null);
        await UpdateAsync(s, ticket, "c-idem", humanFollowUp: true);
        AssertQueuedForAPerson(s, ticket);
        var history = s.F.AssignmentHistory.Entries.Count(h => h.TicketId == ticket.TicketId);
        var transfers = s.F.Audit.Entries.Count(e => e.Action == "Transfer" && e.EntityId == ticket.TicketId.ToString());

        await UpdateAsync(s, ticket, "c-idem", humanFollowUp: true);

        AssertQueuedForAPerson(s, ticket);
        Assert.Equal(history, s.F.AssignmentHistory.Entries.Count(h => h.TicketId == ticket.TicketId));
        Assert.Equal(transfers, s.F.Audit.Entries.Count(e => e.Action == "Transfer" && e.EntityId == ticket.TicketId.ToString()));
        var next = await IngestAsync(s, "c-next", GenesysChannel.WebsiteChat, humanFollowUp: true);
        AssertQueuedForAPerson(s, next);                                         // the next ticket is queued too: nobody is dealt, the cursor never moved
        Assert.True(RotationUntouched(s));
    }

    [Fact]
    public async Task AiConversationThatNeverRequestedAPerson_IsNotMoved()
    {
        var s = Build();
        var ticket = await IngestAsync(s, "c-ai", GenesysChannel.WebsiteChat, humanFollowUp: null);

        await UpdateAsync(s, ticket, "c-ai", humanFollowUp: null);
        await UpdateAsync(s, ticket, "c-ai", humanFollowUp: false);

        Assert.Equal(s.Leasing.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.DoesNotContain(s.F.Audit.Entries, e => e.Action == "Transfer");
    }

    [Fact]
    public async Task HumanOwnedTicket_IsNeverOverwritten_WhenHumanFollowUpIsReportedLater()
    {
        var s = Build();
        var ticket = await IngestAsync(s, "c-owned", GenesysChannel.Phone, humanFollowUp: null);
        var leasingOwner = Guid.NewGuid();
        s.F.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(leasingOwner, s.Leasing.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        ticket.AssignTo(leasingOwner);
        await s.F.TicketAssignments.AddAsync(
            new TicketAssignment(ticket.TicketId, leasingOwner, s.Leasing.DepartmentId, DateTime.UtcNow, assigningActorEmployeeId: leasingOwner));

        await UpdateAsync(s, ticket, "c-owned", humanFollowUp: true);

        Assert.Equal(leasingOwner, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(s.Leasing.DepartmentId, ticket.CurrentDepartmentId);
    }

    [Fact]
    public async Task NoEligibleCallCenterAgent_StillQueuesTheTicketInCallCenter_WithTheAiAsOwner_AndNoAssignmentFailure()
    {
        // Routing to the queue no longer needs an agent: with nobody available the ticket waits there exactly as it does with a full team, and no
        // "could not assign" failure is raised because no assignment is attempted.
        var s = Build(agentCount: 0);
        var ticket = await IngestAsync(s, "c-none", GenesysChannel.SocialMedia, humanFollowUp: null);

        await UpdateAsync(s, ticket, "c-none", humanFollowUp: true);

        AssertQueuedForAPerson(s, ticket);
        Assert.DoesNotContain(s.F.WorkflowEvents.All, e => e.TicketId == ticket.TicketId && e.EventType == WorkflowEventType.ManualAssignmentRequired);
        Assert.True(RotationUntouched(s));
    }
}
