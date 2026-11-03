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
/// (Phone, Website chat, WhatsApp, Social media) must now reach Call Center — at creation when the flag is already known, and on the
/// update otherwise — with ONE Round Robin agent, and without ever overwriting human ownership or touching an AI-only conversation.
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

    private static Task<GenesysTicketUpdateResult> UpdateAsync(Scenario s, Ticket ticket, string conversationId, bool? humanFollowUp) =>
        s.F.TicketUpdate.UpdateAsync(
            ServiceAccount, ticket.TicketId, new GenesysTicketUpdateDto(conversationId, HumanFollowUpRequired: humanFollowUp));

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task HumanFollowUpKnownAtCreation_EntersCallCenter_WithOneRoundRobinAgent(GenesysChannel channel)
    {
        var s = Build();

        var t1 = await IngestAsync(s, "c-1", channel, humanFollowUp: true);
        var t2 = await IngestAsync(s, "c-2", channel, humanFollowUp: true);

        Assert.All(new[] { t1, t2 }, t => Assert.Equal(s.F.CallCenter!.DepartmentId, t.CurrentDepartmentId));
        Assert.All(new[] { t1, t2 }, t => Assert.Equal(s.Leasing.DepartmentId, t.OriginatingDepartmentId));
        Assert.Equal(s.Agents[0], t1.CurrentOwnerEmployeeId);
        Assert.Equal(s.Agents[1], t2.CurrentOwnerEmployeeId);
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task HumanFollowUpReportedAfterCreation_RoutesTheUnassignedTicketToCallCenter_WithOneAgent(GenesysChannel channel)
    {
        var s = Build();

        // Exactly the production symptom: created in Leasing, Open, nobody owns it.
        var ticket = await IngestAsync(s, "c-late", channel, humanFollowUp: null);
        Assert.Equal(s.Leasing.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        var statusBefore = ticket.TicketStatus;

        var update = await UpdateAsync(s, ticket, "c-late", humanFollowUp: true);

        Assert.Equal(GenesysTicketUpdateOutcome.Applied, update.Outcome);
        Assert.Equal(s.F.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(s.Leasing.DepartmentId, ticket.OriginatingDepartmentId);   // preserved
        Assert.Equal(s.Agents[0], ticket.CurrentOwnerEmployeeId);                // exactly one eligible Call Center agent
        Assert.Equal(statusBefore, ticket.TicketStatus);                         // status untouched
        Assert.Single(s.F.TicketAssignments.Added, a => a.TicketId == ticket.TicketId && a.IsCurrent);
        Assert.Contains(s.F.Audit.Entries, e => e.Action == "Transfer" && e.AfterValue!.Contains("routed to Call Center first"));
        Assert.Contains(s.F.Audit.Entries, e => e.Action == "AutoAssign" && e.AfterValue!.Contains("Trigger=CallCenterIntake"));
    }

    [Fact]
    public async Task RepeatedHumanFollowUpUpdate_IsIdempotent_NoSecondAssignmentAndTheRotationDoesNotAdvance()
    {
        var s = Build();
        var ticket = await IngestAsync(s, "c-idem", GenesysChannel.WebsiteChat, humanFollowUp: null);
        await UpdateAsync(s, ticket, "c-idem", humanFollowUp: true);
        var owner = ticket.CurrentOwnerEmployeeId;
        var assignments = s.F.TicketAssignments.Added.Count;

        await UpdateAsync(s, ticket, "c-idem", humanFollowUp: true);

        Assert.Equal(owner, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(assignments, s.F.TicketAssignments.Added.Count);
        var next = await IngestAsync(s, "c-next", GenesysChannel.WebsiteChat, humanFollowUp: true);
        Assert.Equal(s.Agents[1], next.CurrentOwnerEmployeeId);                  // the cursor moved exactly once
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
    public async Task NoEligibleCallCenterAgent_LeavesTheRoutedTicketVisiblyUnassignedInCallCenter()
    {
        var s = Build(agentCount: 0);
        var ticket = await IngestAsync(s, "c-none", GenesysChannel.SocialMedia, humanFollowUp: null);

        await UpdateAsync(s, ticket, "c-none", humanFollowUp: true);

        Assert.Equal(s.F.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Contains(s.F.Audit.Entries, e => e.Action == "AutoAssign" && e.AfterValue!.Contains("Status=Unassigned"));
    }
}
