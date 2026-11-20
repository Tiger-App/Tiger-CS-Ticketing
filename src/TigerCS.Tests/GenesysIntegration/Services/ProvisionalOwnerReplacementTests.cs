using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// The "human needed, nobody accepted yet" lifecycle on every supported channel under the AI-first ownership rules. There is NO provisional Round Robin
/// owner any more: the ticket waits in the Call Center queue with the AI Agent still its owner, no person and no assignment row, and the rotation cursor
/// is untouched. The agent Genesys reports CONNECTED (<c>routing.agentId</c>) is assigned directly. Ownership stays put once a person has worked the
/// ticket or intentionally transferred it, and history, audit and SLA data survive every transition.
/// </summary>
public class ProvisionalOwnerReplacementTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    public static TheoryData<GenesysChannel> AllChannels => new()
    {
        GenesysChannel.Phone, GenesysChannel.WebsiteChat, GenesysChannel.WhatsApp,
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

    private static Task<GenesysTicketUpdateResult> ConnectAsync(World w, long ticketId, string conversationId, string genesysAgentId) =>
        w.F.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId,
            new GenesysTicketUpdateDto(conversationId, Routing: new GenesysRoutingUpdateDto(AgentId: genesysAgentId, ConnectedAtUtc: DateTime.UtcNow)));

    private static bool RotationUntouched(World w) => w.F.Rotation.Rows.Values.All(r => r.LastAssignedEmployeeId is null);

    /// <summary>The state of a human-needed ticket nobody has accepted: Call Center queue, AI Agent owner, no person, no row, no rotation.</summary>
    private static void AssertWaitingInTheQueue(World w, long ticketId)
    {
        var ticket = w.Ticket(ticketId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(w.Cs.DepartmentId, ticket.OriginatingDepartmentId);                       // the originating department is never touched
        Assert.Equal(ServiceAccount, ticket.AiAgentOwnerEmployeeId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.DoesNotContain(w.F.TicketAssignments.Added, a => a.TicketId == ticketId);
        Assert.True(RotationUntouched(w));
    }

    private static async Task<long> IngestAndConnectAsync(World w, string conversationId, GenesysChannel channel, int agentIndex)
    {
        var ticketId = await IngestHumanRequiredAsync(w, conversationId, channel);
        await ConnectAsync(w, ticketId, conversationId, w.Agents[agentIndex].GenesysId);
        Assert.Equal(w.Agents[agentIndex].Id, w.Ticket(ticketId).CurrentOwnerEmployeeId);
        return ticketId;
    }

    // ---- 1: nobody is provisionally assigned any more -------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task AHumanNeededTicket_WaitsInTheCallCenterQueue_WithTheAiOwner_AndNoProvisionalRoundRobinOwner(GenesysChannel channel)
    {
        var w = Build();

        var ticketId = await IngestHumanRequiredAsync(w, "c-queue", channel);

        AssertWaitingInTheQueue(w, ticketId);
        // The ticket was created in the queue, so the only history row is the AI Agent's own; no person-assigning source is ever written.
        Assert.Single(w.F.AssignmentHistory.Entries, h => h.TicketId == ticketId && h.Source == TicketAssignmentSource.GenesysAiAgent);
        Assert.DoesNotContain(w.F.AssignmentHistory.Entries, h => h.TicketId == ticketId && h.Source == TicketAssignmentSource.GenesysHumanFollowUp);
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.Action == "AutoAssign" && e.EntityId == ticketId.ToString() && e.AfterValue!.Contains("Status=Assigned"));
    }

    // ---- 2-3, 5: the connected agent is assigned directly; history, audit and SLA survive -------------------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task ConnectedAgent_IsAssignedDirectly_FromTheAiOwner_KeepingHistoryAuditAndSla(GenesysChannel channel)
    {
        var w = Build();
        var ticketId = await IngestHumanRequiredAsync(w, "c-conn", channel);
        var ticket = w.Ticket(ticketId);
        var confirmed = w.Agents[1];
        var before = (ticket.TicketStatus, ticket.PriorityId, ticket.CreatedAtUtc, ticket.SlaState, ticket.OriginatingDepartmentId);

        await ConnectAsync(w, ticketId, "c-conn", confirmed.GenesysId);

        Assert.Equal(confirmed.Id, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(w.F.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
        Assert.True(RotationUntouched(w));

        // One assignment row, straight to the connected agent (there was never a provisional row to supersede).
        var row = Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == ticketId);
        Assert.True(row.IsCurrent);
        Assert.Equal(confirmed.Id, row.AssignedEmployeeId);

        // History: the AI Agent is the previous owner of the person's acceptance.
        var accepted = Assert.Single(w.F.AssignmentHistory.Entries,
            h => h.TicketId == ticketId && h.NewEmployeeId == confirmed.Id && h.Source is TicketAssignmentSource.GenesysDirectCall or TicketAssignmentSource.GenesysCallTransfer);
        Assert.Equal(ServiceAccount, accepted.PreviousEmployeeId);

        // Audit: the acceptance is on record and there never was an automatic pre-assignment to replace.
        Assert.Contains(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == ticketId.ToString() && e.AfterValue!.Contains($"AssignedEmployeeId={confirmed.Id}"));
        Assert.DoesNotContain(w.F.Audit.Entries, e => e.EntityId == ticketId.ToString() && e.AfterValue is not null && e.AfterValue.Contains("ReplacedAutomaticPreAssignment"));

        // SLA and lifecycle data are not touched by an ownership change.
        Assert.Equal(before, (ticket.TicketStatus, ticket.PriorityId, ticket.CreatedAtUtc, ticket.SlaState, ticket.OriginatingDepartmentId));
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task WhenTheSameAgentIsReportedConnectedAgain_NothingIsRewritten(GenesysChannel channel)
    {
        var w = Build();
        var ticketId = await IngestAndConnectAsync(w, "c-same", channel, 0);
        var owner = w.Ticket(ticketId).CurrentOwnerEmployeeId;
        var rows = w.F.TicketAssignments.Added.Count;
        var history = w.F.AssignmentHistory.Entries.Count;
        static bool IsOwnershipAudit(TigerCS.Tests.CustomerVerification.Fakes.RecordedAuditEntry e) => e.Action is "AutoAssign" or "AssignOnAnswer" or "Transfer" or "Assign";
        var audits = w.F.Audit.Entries.Count(e => e.EntityId == ticketId.ToString() && IsOwnershipAudit(e));

        await ConnectAsync(w, ticketId, "c-same", w.Agents[0].GenesysId);

        Assert.Equal(owner, w.Ticket(ticketId).CurrentOwnerEmployeeId);
        Assert.Equal(rows, w.F.TicketAssignments.Added.Count);
        Assert.Equal(history, w.F.AssignmentHistory.Entries.Count);
        Assert.Equal(audits, w.F.Audit.Entries.Count(e => e.EntityId == ticketId.ToString() && IsOwnershipAudit(e)));
    }

    // ---- 4: a person's work or an intentional transfer is never overwritten --------------------------------------------------------

    public static TheoryData<GenesysChannel, string> ChannelsAndHumanWork
    {
        get
        {
            var data = new TheoryData<GenesysChannel, string>();
            foreach (var channel in Enum.GetValues<GenesysChannel>())
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
    public async Task OnceAPersonHasWorkedTheTicket_AnotherConnectedAgentDoesNotTakeItOver(GenesysChannel channel, string work)
    {
        var w = Build();
        var ticketId = await IngestAndConnectAsync(w, "c-worked", channel, 0);
        var ticket = w.Ticket(ticketId);
        var owner = ticket.CurrentOwnerEmployeeId!.Value;

        switch (work)
        {
            case "in-progress":
                ticket.ChangeStatus(TicketStatus.InProgress);   // the owner started work
                break;
            case "first-response":
                if (ticket.FirstHumanResponseAtUtc is null) ticket.RecordFirstHumanResponse(DateTime.UtcNow);   // a voice connect already recorded it at the connect time
                break;
            default:
                // The owner (or a supervisor) assigned it by hand: a person is the assigning actor.
                w.F.TicketAssignments.Added.Single(a => a.TicketId == ticketId && a.IsCurrent).MarkSuperseded();
                await w.F.TicketAssignments.AddAsync(
                    new TicketAssignment(ticketId, owner, w.F.CallCenter!.DepartmentId, DateTime.UtcNow, assigningActorEmployeeId: owner));
                break;
        }

        await ConnectAsync(w, ticketId, "c-worked", w.Agents[1].GenesysId);

        Assert.Equal(owner, ticket.CurrentOwnerEmployeeId);
        Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == ticketId && a.IsCurrent && a.AssignedEmployeeId == owner);
        Assert.Single(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == ticketId.ToString());     // only the first acceptance
        Assert.True(RotationUntouched(w));
    }

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task AnIntentionalTransferByAPerson_IsNeverReversedByALaterConnectedAgent_AndNobodyIsDealtByRoundRobin(GenesysChannel channel)
    {
        var w = Build();
        var ticketId = await IngestAndConnectAsync(w, "c-xfer", channel, 0);
        var ticket = w.Ticket(ticketId);
        var owner = ticket.CurrentOwnerEmployeeId!.Value;

        // The owner transfers it onward (a person's transfer, recorded as such).
        ticket.TransferToDepartment(w.Cs.DepartmentId);
        await w.F.WorkflowEvents.AddAsync(TicketWorkflowEvent.ForTransfer(
            ticketId, DateTime.UtcNow, actorEmployeeId: owner, TicketTransferReason.WrongDepartment,
            w.F.CallCenter!.DepartmentId, w.Cs.DepartmentId, null, "Handled elsewhere", Guid.NewGuid()));
        w.F.TicketAssignments.Added.Single(a => a.TicketId == ticketId && a.IsCurrent).MarkSuperseded();
        var auditBefore = w.F.Audit.Entries.Count;

        await ConnectAsync(w, ticketId, "c-xfer", w.Agents[1].GenesysId);

        // The person's transfer stands: the department is unchanged and the connected agent does NOT take the ticket. There is no Round Robin
        // fallback to give it to somebody else either: the rotation cursor never moves.
        Assert.Equal(w.Cs.DepartmentId, ticket.CurrentDepartmentId);
        Assert.DoesNotContain(w.F.Audit.Entries.Skip(auditBefore), e => e.Action == "AssignOnAnswer" && e.EntityId == ticketId.ToString());
        Assert.DoesNotContain(w.F.Audit.Entries.Skip(auditBefore), e => e.Action == "AutoAssign" && e.EntityId == ticketId.ToString());
        Assert.DoesNotContain(w.F.TicketAssignments.Added, a => a.TicketId == ticketId && a.IsCurrent);
        Assert.True(RotationUntouched(w));
    }

    // ---- 6: only eligible agents ever take a Call Center ticket; nobody is dealt by rotation -------------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task OnlyAnEligibleConnectedAgentTakesTheTicket_EveryoneElseIsFlagged_AndNoRoundRobinFillsIn(GenesysChannel channel)
    {
        var f = new GenesysServiceFixture(callCenterFirst: true, strictAccounts: true, assignOnAnswer: true);
        var (cs, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var eligible = f.AddAgent("g-ok", "Eligible");

        // (A deactivated employee is excluded by the repository's SQL - Employee.DeactivatedAtUtc - which an in-memory fake cannot model;
        // it is covered against real SQL Server in ProvisionalOwnerSqlServerTests.)
        // Everyone below sits in the Call Center roster (except the last) and must still never take the ticket.
        var ineligible = new List<string>();
        void Add(string id, string name, bool lockedOut = false, bool inCallCenter = true, params string[] roles)
        {
            f.AddAgent(id, name, lockedOut: lockedOut, inCallCenter: inCallCenter, roles: roles);
            ineligible.Add(id);
        }

        Add("g-sup", "Supervisor", roles: Roles.CsSupervisor);
        Add("g-mgr", "Manager", roles: Roles.CsManager);
        Add("g-admin", "Admin", roles: Roles.SystemAdministrator);
        Add("g-ro", "Reader", roles: Roles.ReportingUser);
        Add("g-locked", "Locked", lockedOut: true);
        Add("g-other-dept", "Not In Call Center", inCallCenter: false);
        var service = f.AddAgent("g-svc", "Service Identity");
        f.ServiceIdentities!.Add(service);
        ineligible.Add("g-svc");

        var world = new World { F = f, Cs = cs, Agents = [(eligible, "g-ok")] };
        var i = 0;
        foreach (var genesysId in ineligible)
        {
            var conversationId = $"c-elig-{i++}";
            var ticketId = await IngestHumanRequiredAsync(world, conversationId, channel);
            await ConnectAsync(world, ticketId, conversationId, genesysId);

            var ticket = world.Ticket(ticketId);
            Assert.Null(ticket.CurrentOwnerEmployeeId);                                       // never the ineligible agent, never a rotation pick
            Assert.Equal(ServiceAccount, ticket.AiAgentOwnerEmployeeId);
            Assert.DoesNotContain(f.TicketAssignments.Added, a => a.TicketId == ticketId);
            Assert.Contains(f.Audit.Entries, e => e.Action == "AnswerAssignmentSkipped" && e.EntityId == ticketId.ToString());
        }

        Assert.True(RotationUntouched(world));

        var okTicket = await IngestHumanRequiredAsync(world, "c-elig-ok", channel);
        await ConnectAsync(world, okTicket, "c-elig-ok", "g-ok");
        Assert.Equal(eligible, world.Ticket(okTicket).CurrentOwnerEmployeeId);
    }

    // ---- 7: concurrent / repeated Genesys updates ---------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllChannels))]
    public async Task TwoConnectedEventsForDifferentAgents_LeaveExactlyOneOwnerAndOneCurrentRow_AndRedeliveryChangesNothing(GenesysChannel channel)
    {
        var w = Build();
        var ticketId = await IngestHumanRequiredAsync(w, "c-race", channel);

        // Genesys delivers both "connected" events back to back (the loser may be a stale redelivery).
        await ConnectAsync(w, ticketId, "c-race", w.Agents[0].GenesysId);
        await ConnectAsync(w, ticketId, "c-race", w.Agents[1].GenesysId);
        await ConnectAsync(w, ticketId, "c-race", w.Agents[0].GenesysId);

        var winner = w.Agents[0].Id;
        Assert.Equal(winner, w.Ticket(ticketId).CurrentOwnerEmployeeId);                       // the first confirmed agent stands
        var current = Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == ticketId && a.IsCurrent);
        Assert.Equal(winner, current.AssignedEmployeeId);
        Assert.Single(w.F.TicketAssignments.Added, a => a.TicketId == ticketId);               // no provisional row: just the confirmed one
        Assert.Single(w.F.Audit.Entries, e => e.Action == "AssignOnAnswer" && e.EntityId == ticketId.ToString());
        Assert.True(RotationUntouched(w));
    }
}
