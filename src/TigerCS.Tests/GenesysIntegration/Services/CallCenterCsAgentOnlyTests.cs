using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Business decision: automatic assignment in the Call Center (department code CC) goes to CS Agents ONLY - Round Robin and the confirmed
/// Genesys answering agent alike - while every other department keeps its wider operational set (CS Agent, Department Employee, Department
/// Head). Also: an internal note is human work, so a provisional owner who wrote one is not replaced by a confirmed agent.
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

    // ---- provisional Round Robin ---------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task CallCenterRoundRobin_PicksCsAgentsOnly_NeverADepartmentEmployeeOrHead(GenesysChannel channel)
    {
        var (f, cs) = Build();
        var agents = new[] { f.AddAgent("g-a1", "Agent 1"), f.AddAgent("g-a2", "Agent 2") };
        f.AddAgent("g-emp", "Dept Employee", roles: Roles.DepartmentEmployee);
        f.AddAgent("g-head", "Dept Head", roles: Roles.DepartmentHead);
        f.AddAgent("g-both", "Agent And Employee", roles: [Roles.CsAgent, Roles.DepartmentEmployee]);   // holds CS Agent too: still eligible
        var dual = f.DepartmentAssignments.Assignments.Last().EmployeeId;

        var owners = new List<Guid>();
        for (var i = 0; i < 9; i++)
        {
            owners.Add((await IngestHumanRequiredAsync(f, cs, $"c-rr-{i}", channel)).CurrentOwnerEmployeeId!.Value);
        }

        var allowed = agents.Append(dual).ToHashSet();
        Assert.All(owners, o => Assert.Contains(o, allowed));
        Assert.Equal(allowed.Count, owners.Distinct().Count());
    }

    [Fact]
    public async Task CallCenterWithNoCsAgent_LeavesTheTicketUnownedInTheQueue_NotGivenToAnEmployeeOrHead()
    {
        var (f, cs) = Build();
        f.AddAgent("g-emp", "Dept Employee", roles: Roles.DepartmentEmployee);
        f.AddAgent("g-head", "Dept Head", roles: Roles.DepartmentHead);
        f.AddAgent("g-sup", "Supervisor", roles: Roles.CsSupervisor);

        var ticket = await IngestHumanRequiredAsync(f, cs, "c-none", GenesysChannel.WhatsApp);

        Assert.Equal(f.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Contains(f.Audit.Entries, e => e.Action == "AutoAssign" && e.AfterValue!.Contains("Status=Unassigned"));
    }

    [Fact]
    public async Task OtherDepartments_KeepTheWiderRoundRobinSet_DepartmentEmployeeAndHeadStillReceiveWork()
    {
        var (f, cs) = Build();
        var accounting = f.Departments.AddDepartment("Accounting", "ACC");
        Guid Member(string name, string role)
        {
            var id = f.AddAgent($"g-{name}", name, inCallCenter: false, roles: role);
            f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(id, accounting.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
            return id;
        }

        var wide = new[] { Member("acc-agent", Roles.CsAgent), Member("acc-emp", Roles.DepartmentEmployee), Member("acc-head", Roles.DepartmentHead) };
        var supervisor = Member("acc-sup", Roles.CsSupervisor);                       // still never dealt work by rotation

        var owners = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            // A ticket sitting unowned in Accounting (no human requested), then Accounting's own Round Robin fallback.
            var result = await f.Ingestion.IngestAsync(
                ServiceAccount, new GenesysInquiryDto($"c-acc-{i}", GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", DepartmentId: accounting.DepartmentId));
            var ticket = f.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId);
            Assert.Equal(accounting.DepartmentId, ticket.CurrentDepartmentId);
            var assigned = await f.AutoAssignment.ApplyRoundRobinFallbackAsync(
                ticket, new AutoAssignmentResult(AutoAssignmentOutcome.NoRuleConfigured), AutoAssignmentTrigger.CategoryRouted, DateTime.UtcNow, Guid.NewGuid());
            Assert.Equal(AutoAssignmentOutcome.AssignedRoundRobin, assigned.Outcome);
            owners.Add(ticket.CurrentOwnerEmployeeId!.Value);
        }

        Assert.All(owners, o => Assert.Contains(o, wide));
        Assert.DoesNotContain(supervisor, owners);
        Assert.Equal(3, owners.Distinct().Count());                                   // Department Employee and Head are in the rotation
    }

    // ---- confirmed Genesys answering agent -----------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task ConfirmedAgentWhoIsNotACsAgent_DoesNotTakeTheTicket_ItWaitsFlaggedInTheCallCenterQueue(GenesysChannel channel)
    {
        var (f, cs) = Build();
        f.AddAgent("g-a1", "Agent 1");
        var employee = f.AddAgent("g-emp", "Dept Employee", roles: Roles.DepartmentEmployee);   // Call Center member, operational role, NOT a CS Agent
        var ticket = await IngestHumanRequiredAsync(f, cs, "c-emp", channel);

        await ConnectAsync(f, ticket.TicketId, "c-emp", "g-emp");

        Assert.Equal(f.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
        Assert.NotEqual(employee, ticket.CurrentOwnerEmployeeId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);                                              // never a substitute person
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == ticket.TicketId.ToString());
        Assert.Contains(f.WorkflowEvents.All, e => e.TicketId == ticket.TicketId && e.EventType == WorkflowEventType.ManualAssignmentRequired
            && e.Note!.Contains("CS Agent"));
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task ConfirmedCsAgent_StillReplacesTheProvisionalCsAgent(GenesysChannel channel)
    {
        var (f, cs) = Build();
        var a = f.AddAgent("g-a1", "Agent 1");
        var b = f.AddAgent("g-a2", "Agent 2");
        var ticket = await IngestHumanRequiredAsync(f, cs, "c-ok", channel);
        var provisional = ticket.CurrentOwnerEmployeeId!.Value;
        var confirmed = provisional == a ? "g-a2" : "g-a1";

        await ConnectAsync(f, ticket.TicketId, "c-ok", confirmed);

        Assert.Equal(provisional == a ? b : a, ticket.CurrentOwnerEmployeeId);
    }

    // ---- internal notes are human work ----------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task AnInternalNote_OnTheProvisionalOwnersTicket_BlocksReplacement_WhileNoNoteAllowsIt(GenesysChannel channel)
    {
        var (f, cs) = Build();
        var ids = new Dictionary<Guid, string> { [f.AddAgent("g-a1", "Agent 1")] = "g-a1", [f.AddAgent("g-a2", "Agent 2")] = "g-a2" };
        string OtherGenesysId(Guid owner) => ids.First(kv => kv.Key != owner).Value;

        var plain = await IngestHumanRequiredAsync(f, cs, "c-plain", channel);
        var noted = await IngestHumanRequiredAsync(f, cs, "c-noted", channel);
        var notedOwner = noted.CurrentOwnerEmployeeId!.Value;
        var plainOwner = plain.CurrentOwnerEmployeeId!.Value;

        // The provisional owner writes an internal note: no status change, no reply, no new assignment row - only the note itself.
        await f.TicketNotes.AddAsync(new TicketNote(noted.TicketId, "Called the customer back, waiting for the unit number.", notedOwner, DateTime.UtcNow));

        await ConnectAsync(f, plain.TicketId, "c-plain", OtherGenesysId(plainOwner));
        await ConnectAsync(f, noted.TicketId, "c-noted", OtherGenesysId(notedOwner));

        Assert.NotEqual(plainOwner, plain.CurrentOwnerEmployeeId);                       // no human work: replaced
        Assert.Equal(notedOwner, noted.CurrentOwnerEmployeeId);                          // a note is human work: kept
        Assert.Single(f.TicketAssignments.Added, a => a.TicketId == noted.TicketId && a.IsCurrent && a.AssignedEmployeeId == notedOwner);
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == noted.TicketId.ToString());
    }
}
