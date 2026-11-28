using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The LEGACY provisional-owner lifecycle on every supported messaging channel. TigerCS no longer deals a provisional owner to a Genesys messaging ticket by itself
/// (see <see cref="DigitalChannelsAwaitGenesysConfirmationTests"/>), but a ticket that ALREADY has one (created before the rule, or by an authorised retry) keeps the
/// replace-the-automatic-owner logic: here the provisional owner is created explicitly with <see cref="AutoAssignmentTrigger.ManualRetry"/>, which bypasses the
/// "await Genesys" guard. When Genesys later confirms a different eligible Call Center agent CONNECTED (<c>routing.agentId</c>) that agent becomes the owner - the
/// provisional pick does not block it. Ownership stays put once a person has worked the ticket or intentionally transferred it, and history, audit and SLA data
/// survive every transition.
/// </summary>
public class ProvisionalOwnerReplacementTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    public static TheoryData<GenesysChannel> AllChannels => new()
    {
        GenesysChannel.WebsiteChat, GenesysChannel.WhatsApp,
        GenesysChannel.Instagram, GenesysChannel.Facebook, GenesysChannel.SocialMedia
    };

    private sealed class World
    {
        public required GenesysServiceFixture F { get; init; }
        public required Department Cs { get; init; }
        public required (Guid Id, string GenesysId)[] Agents { get; init; }
        public Ticket Ticket(long id) => F.Tickets.All.Single(t => t.TicketId == id);
        public string GenesysIdOf(Guid id) => Agents.Single(a => a.Id == id).GenesysId;
    }

    private static World Build()
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true, assignOnAnswer: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var agents = Enumerable.Range(1, 3).Select(i => (Id: f.AddAgent($"g-agent-{i}", $"Agent {i}"), GenesysId: $"g-agent-{i}")).ToArray();
        return new World { F = f, Cs = cs, Agents = agents };
    }

    private static async Task<long> IngestHumanRequiredAsync(World w, string conversationId, GenesysChannel channel)
    {
        var result = await w.F.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conversationId, channel, CustomerPhone: "+971500000001", DepartmentId: w.Cs.DepartmentId, HumanFollowUpRequired: true));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        return result.Ticket!.TicketId;
    }

    /// <summary>A messaging ticket that already HAS a provisional automatic owner: ingested (it waits, unowned), then dealt by an authorised retry.</summary>
    private static async Task<long> IngestWithProvisionalOwnerAsync(World w, string conversationId, GenesysChannel channel)
    {
        var ticketId = await IngestHumanRequiredAsync(w, conversationId, channel);
        var ticket = w.Ticket(ticketId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);                                   // the new rule: nobody is dealt at creation
        var dealt = await w.F.AutoAssignment.EnsureCsAgentAsync(ticket, null, AutoAssignmentTrigger.ManualRetry, DateTime.UtcNow, Guid.NewGuid());
        Assert.NotNull(dealt.AssignedEmployeeId);
        Assert.Equal(dealt.AssignedEmployeeId, ticket.CurrentOwnerEmployeeId);
        return ticketId;
    }

    private static Task<GenesysTicketUpdateResult> ConnectAsync(World w, long ticketId, string conversationId, string genesysAgentId) =>
        w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(conversationId, Routing: new GenesysRoutingUpdateDto(AgentId: genesysAgentId, ConnectedAtUtc: DateTime.UtcNow)));

    private static string AnotherAgent(World w, Guid provisional) => w.Agents.First(a => a.Id != provisional).GenesysId;

    // ---- 1-3, 5: the confirmed agent replaces the provisional one; history, audit and SLA survive ----------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task ConfirmedAgent_ReplacesALegacyProvisionalOwner_KeepingHistoryAuditAndSla(GenesysChannel channel)
    {
        var w = Build();
        var ticketId = await IngestWithProvisionalOwnerAsync(w, "c-prov", channel);
        var ticket = w.Ticket(ticketId);
        var provisional = ticket.CurrentOwnerEmployeeId!.Value;
        var confirmedGenesysId = AnotherAgent(w, provisional);
        var confirmed = w.Agents.Single(a => a.GenesysId == confirmedGenesysId).Id;
        var before = (ticket.TicketStatus, ticket.PriorityId, ticket.CreatedAtUtc, ticket.SlaState, ticket.OriginatingDepartmentId);

        await ConnectAsync(w, ticketId, "c-prov", confirmedGenesysId);

        Assert.Equal(confirmed, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);

        // Assignment history: the provisional row is kept (superseded), the confirmed row is the single current one.
        var rows = w.F.TicketAssignments.Added.Where(a => a.TicketId == ticketId).ToList();
        Assert.Equal(2, rows.Count);
        var old = Assert.Single(rows, r => r.AssignedEmployeeId == provisional);
        Assert.False(old.IsCurrent);
        Assert.Null(old.AssigningActorEmployeeId);                       // it really was the automation's pick
        var current = Assert.Single(rows, r => r.IsCurrent);
        Assert.Equal(confirmed, current.AssignedEmployeeId);

        // Audit: both decisions are on record, and the replacement names what it replaced.
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "AutoAssign" && e.EntityId == ticketId.ToString() && e.AfterValue!.Contains(provisional.ToString()));
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == ticketId.ToString()
            && e.AfterValue!.Contains($"AssignedEmployeeId={confirmed}") && e.AfterValue.Contains($"ReplacedAutomaticPreAssignment={provisional}"));

        // SLA and lifecycle data are not touched by an ownership change.
        Assert.Equal(before, (ticket.TicketStatus, ticket.PriorityId, ticket.CreatedAtUtc, ticket.SlaState, ticket.OriginatingDepartmentId));
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task WhenTheProvisionalOwnerIsTheConfirmedAgent_NothingIsRewritten(GenesysChannel channel)
    {
        var w = Build();
        var ticketId = await IngestWithProvisionalOwnerAsync(w, "c-same", channel);
        var provisional = w.Ticket(ticketId).CurrentOwnerEmployeeId!.Value;
        var rows = w.F.TicketAssignments.Added.Count;
        static bool IsOwnershipAudit(TigerCS.Tests.CustomerVerification.Fakes.RecordedAuditEntry e) => e.Action is "AutoAssign" or "AssignOnAnswer" or "Transfer" or "Assign";
        var audits = w.F.Audit.Entries.Count(e => e.EntityId == ticketId.ToString() && IsOwnershipAudit(e));

        await ConnectAsync(w, ticketId, "c-same", w.GenesysIdOf(provisional));

        Assert.Equal(provisional, w.Ticket(ticketId).CurrentOwnerEmployeeId);
        Assert.Equal(rows, w.F.TicketAssignments.Added.Count);
        Assert.Equal(audits, w.F.Audit.Entries.Count(e => e.EntityId == ticketId.ToString() && IsOwnershipAudit(e)));
    }

    // ---- 4: a person's work or an intentional transfer is never overwritten --------------------------------------------------------

    public static TheoryData<GenesysChannel, string> ChannelsAndHumanWork
    {
        get
        {
            var data = new TheoryData<GenesysChannel, string>();
            foreach (var channel in Enum.GetValues<GenesysChannel>().Where(c => c != GenesysChannel.Phone))   // VOICE: see VoiceOwnershipRulesTests
            {
                foreach (var work in new[] { "in-progress", "first-response", "manual-assignment" })
                {
                    data.Add(channel, work);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ChannelsAndHumanWork))]
    public async Task OnceAPersonHasWorkedTheTicket_TheConfirmedAgentDoesNotTakeItOver(GenesysChannel channel, string work)
    {
        var w = Build();
        var ticketId = await IngestWithProvisionalOwnerAsync(w, "c-worked", channel);
        var ticket = w.Ticket(ticketId);
        var owner = ticket.CurrentOwnerEmployeeId!.Value;

        switch (work)
        {
            case "in-progress":
                ticket.ChangeStatus(TicketStatus.InProgress);   // the owner started work
                break;
            case "first-response":
                ticket.RecordFirstHumanResponse(DateTime.UtcNow);
                break;
            default:
                // The owner (or a supervisor) assigned it by hand: a person is the assigning actor, so it is no longer a provisional pick.
                w.F.TicketAssignments.Added.Single(a => a.TicketId == ticketId && a.IsCurrent).MarkSuperseded();
                await w.F.TicketAssignments.AddAsync(
                    new TicketAssignment(ticketId, owner, w.F.CallCenter!.DepartmentId, DateTime.UtcNow, assigningActorEmployeeId: owner));
                break;
        }

        await ConnectAsync(w, ticketId, "c-worked", AnotherAgent(w, owner));

        Assert.Equal(owner, ticket.CurrentOwnerEmployeeId);
        Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == ticketId && a.IsCurrent && a.AssignedEmployeeId == owner);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == ticketId.ToString());
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task AnIntentionalTransferByAPerson_IsNeverReversedByTheProvisionalOrConfirmedAgent(GenesysChannel channel)
    {
        var w = Build();
        var ticketId = await IngestWithProvisionalOwnerAsync(w, "c-xfer", channel);
        var ticket = w.Ticket(ticketId);
        var provisional = ticket.CurrentOwnerEmployeeId!.Value;

        // The provisional owner transfers it onward (a person's transfer, recorded as such).
        ticket.TransferToDepartment(w.Cs.DepartmentId);
        await w.F.WorkflowEvents.AddAsync(TicketWorkflowEvent.ForTransfer(
            ticketId, DateTime.UtcNow, actorEmployeeId: provisional, TicketTransferReason.WrongDepartment,
            w.F.CallCenter!.DepartmentId, w.Cs.DepartmentId, null, "Handled elsewhere", Guid.NewGuid()));
        w.F.TicketAssignments.Added.Single(a => a.TicketId == ticketId && a.IsCurrent).MarkSuperseded();

        await ConnectAsync(w, ticketId, "c-xfer", AnotherAgent(w, provisional));

        // The person's transfer stands: the department is unchanged and the connected agent does NOT take the ticket (no AssignOnAnswer). The setup
        // above clears the owner; on a Genesys messaging channel the shared rotation no longer fills the gap (the destination department's own
        // assignment, run by the real transfer, is what deals it), so nobody is dealt here.
        Assert.Equal(w.Cs.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.DoesNotContain(w.F.TicketAssignments.Added, a => a.TicketId == ticketId && a.IsCurrent);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == ticketId.ToString());
    }

    // ---- 6: only eligible agents ever receive an automatic Call Center assignment ---------------------------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task AnAuthorisedRetry_NeverPicksAnIneligibleCallCenterMember_WhileCreationDealsNobody(GenesysChannel channel)
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true, assignOnAnswer: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var eligible = new[] { f.AddAgent("g-ok-1", "Eligible 1"), f.AddAgent("g-ok-2", "Eligible 2") };

        // (A deactivated employee is excluded by the repository's SQL - Employee.DeactivatedAtUtc - which an in-memory fake cannot model;
        // it is covered against real SQL Server in ProvisionalOwnerSqlServerTests.)
        // Everyone below sits in the Call Center roster and must still never be handed a ticket by rotation.
        f.AddAgent("g-sup", "Supervisor", roles: Roles.CsSupervisor);
        f.AddAgent("g-mgr", "Manager", roles: Roles.CsManager);
        f.AddAgent("g-admin", "Admin", roles: Roles.SystemAdministrator);
        f.AddAgent("g-ro", "Reader", roles: Roles.ReportingUser);
        var locked = f.AddAgent("g-locked", "Locked", lockedOut: true);
        var service = f.AddAgent("g-svc", "Service Identity");
        f.ServiceIdentities!.Add(service);
        var otherDept = f.AddAgent("g-other-dept", "Not In Call Center", inCallCenter: false);

        var owners = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            var result = await f.Ingestion.IngestAsync(
                ServiceAccount,
                new GenesysInquiryDto($"c-elig-{i}", channel, CustomerPhone: "+971500000001", DepartmentId: cs.DepartmentId, HumanFollowUpRequired: true));
            var ticket = f.Tickets.All.Single(t => t.TicketId == result.Ticket!.TicketId);
            Assert.Null(ticket.CurrentOwnerEmployeeId);                                  // creation deals nobody on a messaging channel
            var dealt = await f.AutoAssignment.EnsureCsAgentAsync(ticket, null, AutoAssignmentTrigger.ManualRetry, DateTime.UtcNow, Guid.NewGuid());
            owners.Add(dealt.AssignedEmployeeId!.Value);                                 // the authorised retry is the only thing that still rotates
        }

        var names = new Dictionary<Guid, string>
        {
            [locked] = "locked", [service] = "service", [otherDept] = "not-in-call-center"
        };
        Assert.All(owners, o => Assert.True(eligible.Contains(o), "picked ineligible: " + names.GetValueOrDefault(o, "?")));
        Assert.Equal(eligible.Length, owners.Distinct().Count());       // and the rotation really spreads across the eligible ones
    }

    // ---- 7: concurrent / repeated Genesys updates ---------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task TwoConnectedEventsForDifferentAgents_LeaveExactlyOneOwnerAndOneCurrentRow_AndRedeliveryChangesNothing(GenesysChannel channel)
    {
        var w = Build();
        var ticketId = await IngestWithProvisionalOwnerAsync(w, "c-race", channel);
        var provisional = w.Ticket(ticketId).CurrentOwnerEmployeeId!.Value;
        var contenders = w.Agents.Where(a => a.Id != provisional).Select(a => a.GenesysId).ToArray();

        // Genesys delivers both "connected" events back to back (the loser may be a stale redelivery).
        await ConnectAsync(w, ticketId, "c-race", contenders[0]);
        await ConnectAsync(w, ticketId, "c-race", contenders[1]);
        await ConnectAsync(w, ticketId, "c-race", contenders[0]);

        var winner = w.Agents.Single(a => a.GenesysId == contenders[0]).Id;
        Assert.Equal(winner, w.Ticket(ticketId).CurrentOwnerEmployeeId);                       // the first confirmed agent stands
        var current = Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == ticketId && a.IsCurrent);
        Assert.Equal(winner, current.AssignedEmployeeId);
        Assert.Equal(2, w.F.TicketAssignments.Added.Count(a => a.TicketId == ticketId));      // provisional (superseded) + confirmed
        Assert.Single(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == ticketId.ToString());
    }

}
