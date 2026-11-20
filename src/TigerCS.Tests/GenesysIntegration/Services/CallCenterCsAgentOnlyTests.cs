using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Business decision: a person receives a Call Center ticket only as a CS Agent - the confirmed Genesys answering agent must be one - while every other
/// department keeps its wider operational set (CS Agent, Department Employee, Department Head) for Round Robin of NON-Genesys work. A human-needed
/// Genesys ticket is never dealt by rotation: it waits in the Call Center queue with the AI Agent as owner until an eligible CS Agent is reported
/// connected. Also: an internal note is human work, so an owner who wrote one is not replaced by another connected agent.
/// </summary>
public class CallCenterCsAgentOnlyTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    public static TheoryData<GenesysChannel> AllChannels => new()
    {
        GenesysChannel.Phone, GenesysChannel.WebsiteChat, GenesysChannel.WhatsApp,
        GenesysChannel.Instagram, GenesysChannel.Facebook, GenesysChannel.SocialMedia
    };

    private static (GenesysServiceFixture F, Department Cs) Build()
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true, assignOnAnswer: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        return (f, cs);
    }

    private static async Task<Ticket> IngestHumanRequiredAsync(GenesysServiceFixture f, Department cs, string conversationId, GenesysChannel channel)
    {
        var result = await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conversationId, channel, CustomerPhone: "+971500000001", DepartmentId: cs.DepartmentId, HumanFollowUpRequired: true));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return f.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId);
    }

    private static Task<GenesysTicketUpdateResult> ConnectAsync(GenesysServiceFixture f, long ticketId, string conversationId, string genesysAgentId) =>
        f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(conversationId, Routing: new GenesysRoutingUpdateDto(AgentId: genesysAgentId, ConnectedAtUtc: DateTime.UtcNow)));

    private static bool RotationUntouched(GenesysServiceFixture f) => f.Rotation.Rows.Values.All(r => r.LastAssignedEmployeeId is null);

    private static void AssertWaitingInTheCallCenterQueue(GenesysServiceFixture f, Ticket ticket)
    {
        Assert.Equal(f.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(ServiceAccount, ticket.AiAgentOwnerEmployeeId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.DoesNotContain(f.TicketAssignments.Added, a => a.TicketId == ticket.TicketId);
    }

    // ---- no Round Robin for a human-needed Genesys ticket ---------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task HumanNeededTickets_AreNeverDealtByRoundRobin_WhoeverIsInTheCallCenter(GenesysChannel channel)
    {
        var (f, cs) = Build();
        f.AddAgent("g-a1", "Agent 1");
        f.AddAgent("g-a2", "Agent 2");
        f.AddAgent("g-emp", "Dept Employee", roles: Roles.DepartmentEmployee);
        f.AddAgent("g-head", "Dept Head", roles: Roles.DepartmentHead);
        f.AddAgent("g-both", "Agent And Employee", roles: [Roles.CsAgent, Roles.DepartmentEmployee]);

        for (var i = 0; i < 9; i++)
        {
            var ticket = await IngestHumanRequiredAsync(f, cs, $"c-rr-{i}", channel);
            AssertWaitingInTheCallCenterQueue(f, ticket);
        }

        Assert.True(RotationUntouched(f));                                                  // the cursor never moved
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AutoAssign" && e.AfterValue!.Contains("Status=Assigned"));
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task OnlyACsAgentReportedConnected_TakesACallCenterTicket_IncludingOneWhoAlsoHoldsAnotherRole(GenesysChannel channel)
    {
        var (f, cs) = Build();
        var agent = f.AddAgent("g-a1", "Agent 1");
        f.AddAgent("g-emp", "Dept Employee", roles: Roles.DepartmentEmployee);
        f.AddAgent("g-head", "Dept Head", roles: Roles.DepartmentHead);
        f.AddAgent("g-both", "Agent And Employee", roles: [Roles.CsAgent, Roles.DepartmentEmployee]);   // holds CS Agent too: still eligible
        var dual = f.DepartmentAssignments.Assignments.Last().EmployeeId;

        var byAgent = await IngestHumanRequiredAsync(f, cs, "c-agent", channel);
        var byDual = await IngestHumanRequiredAsync(f, cs, "c-dual", channel);
        var byEmployee = await IngestHumanRequiredAsync(f, cs, "c-employee", channel);
        var byHead = await IngestHumanRequiredAsync(f, cs, "c-head", channel);

        await ConnectAsync(f, byAgent.TicketId, "c-agent", "g-a1");
        await ConnectAsync(f, byDual.TicketId, "c-dual", "g-both");
        await ConnectAsync(f, byEmployee.TicketId, "c-employee", "g-emp");
        await ConnectAsync(f, byHead.TicketId, "c-head", "g-head");

        Assert.Equal(agent, byAgent.CurrentOwnerEmployeeId);
        Assert.Equal(dual, byDual.CurrentOwnerEmployeeId);
        AssertWaitingInTheCallCenterQueue(f, byEmployee);                                    // flagged, not assigned, not dealt to someone else
        AssertWaitingInTheCallCenterQueue(f, byHead);
        Assert.True(RotationUntouched(f));
    }

    [Fact]
    public async Task CallCenterWithNoCsAgent_LeavesTheTicketInTheQueueWithTheAiOwner_NotGivenToAnEmployeeOrHead()
    {
        var (f, cs) = Build();
        f.AddAgent("g-emp", "Dept Employee", roles: Roles.DepartmentEmployee);
        f.AddAgent("g-head", "Dept Head", roles: Roles.DepartmentHead);
        f.AddAgent("g-sup", "Supervisor", roles: Roles.CsSupervisor);

        var ticket = await IngestHumanRequiredAsync(f, cs, "c-none", GenesysChannel.WhatsApp);

        AssertWaitingInTheCallCenterQueue(f, ticket);
        Assert.True(RotationUntouched(f));
    }

    [Fact]
    public async Task OtherDepartments_StillGetCsAgentsFromTheSharedRotation_DepartmentEmployeeAndHeadAreNotInThePool()
    {
        var (f, cs) = Build();
        var agents = new[] { f.AddAgent("g-a1", "Agent 1"), f.AddAgent("g-a2", "Agent 2") };
        var accounting = f.Departments.AddDepartment("Accounting", "ACC");
        foreach (var (name, role) in new[] { ("acc-emp", Roles.DepartmentEmployee), ("acc-head", Roles.DepartmentHead), ("acc-sup", Roles.CsSupervisor) })
        {
            var id = f.AddAgent($"g-{name}", name, inCallCenter: false, roles: role);
            f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(id, accounting.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        }

        var owners = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            // A ticket sitting unowned in Accounting (no human requested), then the shared fallback.
            var result = await f.Ingestion.IngestAsync(
                ServiceAccount, new GenesysInquiryDto($"c-acc-{i}", GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", DepartmentId: accounting.DepartmentId));
            var ticket = f.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId);
            Assert.Equal(accounting.DepartmentId, ticket.CurrentDepartmentId);
            var assigned = await f.AutoAssignment.ApplyRoundRobinFallbackAsync(
                ticket, new AutoAssignmentResult(AutoAssignmentOutcome.NoRuleConfigured), AutoAssignmentTrigger.CategoryRouted, DateTime.UtcNow, Guid.NewGuid());
            Assert.Equal(AutoAssignmentOutcome.AssignedRoundRobin, assigned.Outcome);
            Assert.Equal(accounting.DepartmentId, ticket.CurrentDepartmentId);       // the department is untouched
            owners.Add(ticket.CurrentOwnerEmployeeId!.Value);
        }

        Assert.All(owners, o => Assert.Contains(o, agents));
        Assert.Equal(2, owners.Distinct().Count());
    }

    // ---- confirmed Genesys answering agent -----------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task ConfirmedAgentWhoIsNotACsAgent_DoesNotTakeTheTicket_ItIsFlaggedInTheQueue_AndNobodyElseIsDealtIn(GenesysChannel channel)
    {
        var (f, cs) = Build();
        f.AddAgent("g-a1", "Agent 1");                                                          // an eligible CS Agent exists, and must NOT be dealt the ticket
        var employee = f.AddAgent("g-emp", "Dept Employee", roles: Roles.DepartmentEmployee);   // Call Center member, operational role, NOT a CS Agent
        var ticket = await IngestHumanRequiredAsync(f, cs, "c-emp", channel);

        await ConnectAsync(f, ticket.TicketId, "c-emp", "g-emp");

        Assert.NotEqual(employee, ticket.CurrentOwnerEmployeeId);
        AssertWaitingInTheCallCenterQueue(f, ticket);                                           // AI owner kept, no person, no row
        Assert.True(RotationUntouched(f));
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == ticket.TicketId.ToString());
        Assert.Contains(f.Audit.Entries, e => e.Action == "AnswerAssignmentSkipped" && e.EntityId == ticket.TicketId.ToString());
        Assert.Contains(f.WorkflowEvents.All, e => e.TicketId == ticket.TicketId && e.EventType == WorkflowEventType.ManualAssignmentRequired
            && e.Note!.Contains("CS Agent"));
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task ConfirmedCsAgent_TakesTheQueuedTicketDirectly_FromTheAiOwner(GenesysChannel channel)
    {
        var (f, cs) = Build();
        var a = f.AddAgent("g-a1", "Agent 1");
        var b = f.AddAgent("g-a2", "Agent 2");
        var ticket = await IngestHumanRequiredAsync(f, cs, "c-ok", channel);
        AssertWaitingInTheCallCenterQueue(f, ticket);

        await ConnectAsync(f, ticket.TicketId, "c-ok", "g-a2");

        Assert.Equal(b, ticket.CurrentOwnerEmployeeId);                                         // THAT agent, not the next in a rotation
        Assert.NotEqual(a, ticket.CurrentOwnerEmployeeId);
        var row = Assert.Single(f.TicketAssignments.Added, x => x.TicketId == ticket.TicketId);
        Assert.True(row.IsCurrent);
        Assert.Equal(b, row.AssignedEmployeeId);
        Assert.True(RotationUntouched(f));
    }

    // ---- internal notes are human work ----------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task AnInternalNote_OnTheOwnersTicket_BlocksReplacement_AndOnAQueuedTicketItAssignsNobody(GenesysChannel channel)
    {
        var (f, cs) = Build();
        var a1 = f.AddAgent("g-a1", "Agent 1");
        f.AddAgent("g-a2", "Agent 2");

        var queued = await IngestHumanRequiredAsync(f, cs, "c-queued", channel);
        var noted = await IngestHumanRequiredAsync(f, cs, "c-noted", channel);
        await ConnectAsync(f, noted.TicketId, "c-noted", "g-a1");
        Assert.Equal(a1, noted.CurrentOwnerEmployeeId);

        // Agent 1 writes an internal note on their ticket: no status change, no reply, no new assignment row - only the note itself.
        await f.TicketNotes.AddAsync(new TicketNote(noted.TicketId, "Called the customer back, waiting for the unit number.", a1, DateTime.UtcNow));
        // Someone writes a note on the queued ticket nobody has accepted.
        await f.TicketNotes.AddAsync(new TicketNote(queued.TicketId, "Customer rang again.", a1, DateTime.UtcNow));

        await ConnectAsync(f, noted.TicketId, "c-noted", "g-a2");

        Assert.Equal(a1, noted.CurrentOwnerEmployeeId);                                         // a note is human work: kept
        Assert.Single(f.TicketAssignments.Added, x => x.TicketId == noted.TicketId && x.IsCurrent && x.AssignedEmployeeId == a1);
        Assert.Single(f.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == noted.TicketId.ToString());     // only the first acceptance
        AssertWaitingInTheCallCenterQueue(f, queued);                                           // a note assigns nobody and does not move the ticket
        Assert.True(RotationUntouched(f));
    }
}
