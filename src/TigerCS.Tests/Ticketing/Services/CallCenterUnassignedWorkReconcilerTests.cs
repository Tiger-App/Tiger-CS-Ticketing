using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// The safety net for human work with no owner: routing (Call Center first), assignment (answering agent, else Round Robin), no
/// redistribution of owned tickets, protection of AI conversations, idempotency and concurrency.
/// </summary>
public class CallCenterUnassignedWorkReconcilerTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);

    internal sealed class FakeRotation : IAssignmentRotationRepository
    {
        public Dictionary<int, DepartmentAssignmentRotation> Rows { get; } = [];
        public Task<DepartmentAssignmentRotation> GetForUpdateAsync(int departmentId, DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            if (!Rows.TryGetValue(departmentId, out var row)) Rows[departmentId] = row = new DepartmentAssignmentRotation(departmentId, nowUtc);
            return Task.FromResult(row);
        }
    }

    internal sealed class StubEvidence : ITicketUnassignedWorkRepository
    {
        public Dictionary<long, UnassignedTicketEvidence> Rows { get; } = [];
        public Task<IReadOnlyList<UnassignedTicketEvidence>> ListOpenUnownedAsync(long afterTicketId, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<UnassignedTicketEvidence>>([.. Rows.Values.Where(r => r.TicketId > afterTicketId && r.OwnerEmployeeId == null).OrderBy(r => r.TicketId).Take(limit)]);
        public Task<UnassignedTicketEvidence?> GetEvidenceAsync(long ticketId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows.GetValueOrDefault(ticketId));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    internal sealed class Env
    {
        public FakeDepartmentRepository Departments { get; } = new();
        public FakeTicketRepository Tickets { get; } = new();
        public FakeAuditEntryWriter Audit { get; } = new();
        public FakeUserDepartmentAssignmentRepository Members { get; } = new();
        public FakeTicketAssignmentRepository Assignments { get; } = new();
        public FakeTicketWorkflowEventRepository Events { get; } = new();
        public FakeRotation Rotation { get; } = new();
        public FakeTicketingUnitOfWork UnitOfWork { get; } = new();
        public StubEvidence Evidence { get; } = new();
        public FakeUserRoleReader RoleReader { get; } = new();
        public Department CallCenter { get; }
        public Department Leasing { get; }
        public CallCenterUnassignedWorkReconciler Reconciler { get; }

        public Env()
        {
            CallCenter = Departments.AddDepartment("Call Center", "CC");
            Leasing = Departments.AddDepartment("Leasing Customer Services", "LCS");
            var engine = new TicketAutoAssignmentService(
                new FakeRequestTypeAssignmentRuleRepository(), new FakeDepartmentWorkflowSettingsRepository(), Members, Assignments, Audit,
                Rotation, Departments, roleReader: RoleReader, workflowEventRepository: Events);
            Reconciler = new CallCenterUnassignedWorkReconciler(Evidence, Tickets, engine, UnitOfWork, new FixedClock());
        }

        public Guid[] AddCallCenterAgents(int count) =>
            [.. Enumerable.Range(0, count).Select(_ =>
            {
                var id = Guid.NewGuid();
                Members.Assignments.Add(new UserDepartmentAssignment(id, CallCenter.DepartmentId, isPrimary: true, Now, assignedByEmployeeId: null));
                RoleReader.Add(id, Roles.CsAgent);
                return id;
            }).OrderBy(i => i)];

        /// <summary>A Call Center member who is eligible for ordinary rotation but is NOT a CS Agent (Department Employee).</summary>
        public Guid AddNonCsAgentMember()
        {
            var id = Guid.NewGuid();
            Members.Assignments.Add(new UserDepartmentAssignment(id, CallCenter.DepartmentId, isPrimary: true, Now, assignedByEmployeeId: null));
            RoleReader.Add(id, Roles.DepartmentEmployee);
            return id;
        }

        /// <summary>An Open ticket sitting in the Leasing queue, plus the evidence about it.</summary>
        public async Task<Ticket> AddQueuedTicketAsync(Func<UnassignedTicketEvidence, UnassignedTicketEvidence>? shape = null, Department? department = null, bool owned = false)
        {
            var dept = department ?? Leasing;
            var ticket = Ticket.CreateUnclassified($"TG-LCS-20261102-{Tickets.All.Count + 1:D4}", dept.DepartmentId, "Lease question", Now.AddDays(-1), null, null, defaultPriorityId: null);
            await Tickets.AddAsync(ticket);
            if (owned) ticket.AssignTo(Guid.NewGuid());
            var evidence = new UnassignedTicketEvidence(
                ticket.TicketId, ticket.TicketNumber, TicketStatus.Open, ticket.CurrentOwnerEmployeeId, dept.DepartmentId, ticket.OriginatingDepartmentId,
                Now.AddDays(-1), false, false, false, false, false, false, null, null, false, null, Now.AddDays(-1), false, false);
            Evidence.Rows[ticket.TicketId] = shape?.Invoke(evidence) ?? evidence;
            return ticket;
        }
    }

    private static CallCenterReconciliationOptions Live() => new() { Enabled = true, DryRun = false, MinTicketAgeMinutes = 3 };

    [Fact]
    public async Task ManualTicketStuckInADepartmentQueue_IsMovedToTheCallCenter_AndDealtToOneAgent()
    {
        var env = new Env();
        var agents = env.AddCallCenterAgents(2);
        var ticket = await env.AddQueuedTicketAsync();

        var result = await env.Reconciler.TryReconcileAsync(ticket.TicketId, Live(), dryRun: false);

        Assert.Equal(ReconcileOutcome.AssignedRoundRobin, result.Outcome);
        Assert.Equal(env.CallCenter.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(env.Leasing.DepartmentId, ticket.OriginatingDepartmentId);   // origin preserved
        Assert.Equal(agents[0], ticket.CurrentOwnerEmployeeId);
        Assert.Single(env.Assignments.Added, a => a.TicketId == ticket.TicketId && a.IsCurrent);
        Assert.Contains(env.Audit.Entries, w => w.Action == "AutoAssign");
    }

    [Fact]
    public async Task FailedGenesysRouting_HumanFlaggedTicket_GetsRoutedAndAssigned()
    {
        var env = new Env();
        var agents = env.AddCallCenterAgents(1);
        var ticket = await env.AddQueuedTicketAsync(e => e with { HasGenesysInteraction = true, HasLiveGenesysInteraction = true, RequiresHumanFollowUp = true });

        var result = await env.Reconciler.TryReconcileAsync(ticket.TicketId, Live(), false);

        Assert.Equal(UnassignedTicketClass.HumanRequiredNotRouted, result.Class);
        Assert.Equal(agents[0], ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task AnsweringAgent_GetsTheTicketDirectly_AndTheRotationDoesNotAdvance()
    {
        var env = new Env();
        var agents = env.AddCallCenterAgents(3);
        var answering = agents[2];
        var ticket = await env.AddQueuedTicketAsync(e => e with { HasGenesysInteraction = true, HasLiveGenesysInteraction = true, AnsweringEmployeeId = answering, AnsweringGenesysUserId = "gen-7" });

        var result = await env.Reconciler.TryReconcileAsync(ticket.TicketId, Live(), false);

        Assert.Equal(ReconcileOutcome.AssignedToAnsweringAgent, result.Outcome);
        Assert.Equal(answering, ticket.CurrentOwnerEmployeeId);
        Assert.Empty(env.Rotation.Rows);   // Round Robin was not consulted
    }

    [Fact]
    public async Task UnusableAnsweringAgent_FallsBackToRoundRobin_NeverLeavesItUnowned()
    {
        var env = new Env();
        var agents = env.AddCallCenterAgents(2);
        var notACallCenterMember = Guid.NewGuid();
        var ticket = await env.AddQueuedTicketAsync(e => e with { HasGenesysInteraction = true, HasLiveGenesysInteraction = true, AnsweringEmployeeId = notACallCenterMember, AnsweringGenesysUserId = "gen-9" });

        var result = await env.Reconciler.TryReconcileAsync(ticket.TicketId, Live(), false);

        Assert.Equal(ReconcileOutcome.AssignedRoundRobin, result.Outcome);
        Assert.Equal(agents[0], ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task PhoneCallAnsweredByAnUnmappedAgent_IsRoundRobined()
    {
        var env = new Env();
        var agents = env.AddCallCenterAgents(1);
        var ticket = await env.AddQueuedTicketAsync(e => e with { HasGenesysInteraction = true, HasLiveGenesysInteraction = true, PhonePickupByUnmappedAgent = true, UnmappedGenesysAgentId = "gen-x" });

        Assert.Equal(ReconcileOutcome.AssignedRoundRobin, (await env.Reconciler.TryReconcileAsync(ticket.TicketId, Live(), false)).Outcome);
        Assert.Equal(agents[0], ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task InCallCenterQueue_UnassignedBecauseNoAgentWasEligibleBefore_IsAssignedOnceAgentsExist()
    {
        var env = new Env();
        var ticket = await env.AddQueuedTicketAsync(e => e with { CurrentDepartmentId = env.CallCenter.DepartmentId, HasGenesysInteraction = true, RequiresHumanFollowUp = true }, env.CallCenter);

        var before = await env.Reconciler.TryReconcileAsync(ticket.TicketId, Live(), false);
        Assert.Equal(ReconcileOutcome.NoEligibleAgent, before.Outcome);   // reported, left queued, nothing invented
        Assert.Null(ticket.CurrentOwnerEmployeeId);

        var agents = env.AddCallCenterAgents(1);
        var after = await env.Reconciler.TryReconcileAsync(ticket.TicketId, Live(), false);
        Assert.Equal(ReconcileOutcome.AssignedRoundRobin, after.Outcome);
        Assert.Equal(agents[0], ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task OwnedTickets_AreNeverRedistributed()
    {
        var env = new Env();
        env.AddCallCenterAgents(2);
        var ticket = await env.AddQueuedTicketAsync(owned: true);
        var owner = ticket.CurrentOwnerEmployeeId;

        var listed = await env.Reconciler.ListAsync(0, Live());
        Assert.Empty(listed);   // an owned ticket is not even a candidate

        // ...and if one slips through (owner set between list and act) the in-transaction re-check protects it.
        env.Evidence.Rows[ticket.TicketId] = env.Evidence.Rows[ticket.TicketId] with { OwnerEmployeeId = null };
        var result = await env.Reconciler.TryReconcileAsync(ticket.TicketId, Live(), false);
        Assert.Equal(ReconcileOutcome.NoLongerApplicable, result.Outcome);
        Assert.Equal(owner, ticket.CurrentOwnerEmployeeId);
        Assert.Empty(env.Assignments.Added);
    }

    [Fact]
    public async Task ActiveAi_CompletedAi_PendingApproval_AndTransferredOut_AreLeftUntouched()
    {
        var env = new Env();
        env.AddCallCenterAgents(2);
        var active = await env.AddQueuedTicketAsync(e => e with { HasGenesysInteraction = true, HasLiveGenesysInteraction = true, LastActivityUtc = Now.AddMinutes(-2) });
        var completed = await env.AddQueuedTicketAsync(e => e with { HasGenesysInteraction = true });
        var approval = await env.AddQueuedTicketAsync(e => e with { PendingApproval = true, RequiresHumanFollowUp = true });
        var transferred = await env.AddQueuedTicketAsync(e => e with { LeftCallCenter = true, RequiresHumanFollowUp = true });

        foreach (var t in new[] { active, completed, approval, transferred })
        {
            var r = await env.Reconciler.TryReconcileAsync(t.TicketId, Live(), false);
            Assert.Equal(ReconcileOutcome.Skipped, r.Outcome);
            Assert.Null(t.CurrentOwnerEmployeeId);
            Assert.Equal(env.Leasing.DepartmentId, t.CurrentDepartmentId);
        }

        Assert.Empty(env.Assignments.Added);
        Assert.Empty(env.Audit.Entries);
    }

    [Fact]
    public async Task DryRun_WritesNothing_ButReportsWhatItWouldDo()
    {
        var env = new Env();
        env.AddCallCenterAgents(1);
        var ticket = await env.AddQueuedTicketAsync();

        var result = await env.Reconciler.TryReconcileAsync(ticket.TicketId, new CallCenterReconciliationOptions(), dryRun: true);

        Assert.Equal(ReconcileOutcome.WouldAct, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(env.Leasing.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Empty(env.Audit.Entries);
        Assert.Equal(0, env.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task ARunTwice_IsIdempotent_TheSecondFindsAnOwnerAndWritesNothing()
    {
        var env = new Env();
        env.AddCallCenterAgents(2);
        var ticket = await env.AddQueuedTicketAsync();

        await env.Reconciler.TryReconcileAsync(ticket.TicketId, Live(), false);
        var owner = ticket.CurrentOwnerEmployeeId;
        var auditCount = env.Audit.Entries.Count;
        env.Evidence.Rows[ticket.TicketId] = env.Evidence.Rows[ticket.TicketId] with { OwnerEmployeeId = owner, CurrentDepartmentId = env.CallCenter.DepartmentId };

        var second = await env.Reconciler.TryReconcileAsync(ticket.TicketId, Live(), false);

        Assert.Equal(ReconcileOutcome.Skipped, second.Outcome);
        Assert.Equal(owner, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(auditCount, env.Audit.Entries.Count);
    }

    [Fact]
    public async Task ManyTickets_AreDealtEvenlyAcrossAgents_NoAgentGetsTwoInARow()
    {
        var env = new Env();
        var agents = env.AddCallCenterAgents(3);
        var tickets = new List<Ticket>();
        for (var i = 0; i < 9; i++) tickets.Add(await env.AddQueuedTicketAsync());

        foreach (var t in tickets) await env.Reconciler.TryReconcileAsync(t.TicketId, Live(), false);

        Assert.All(agents, a => Assert.Equal(3, tickets.Count(t => t.CurrentOwnerEmployeeId == a)));
    }

    [Fact]
    public async Task LostRace_IsReported_NotThrown_AndNeverLeavesTwoOwners()
    {
        var env = new Env();
        env.AddCallCenterAgents(2);
        var ticket = await env.AddQueuedTicketAsync();
        env.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = 1;   // a person assigned it at the same instant

        var result = await env.Reconciler.TryReconcileAsync(ticket.TicketId, Live(), false);

        Assert.Equal(ReconcileOutcome.LostRace, result.Outcome);
        Assert.Equal(1, env.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, env.UnitOfWork.TransactionsCommitted);   // nothing committed by the loser
    }

    [Fact]
    public async Task DuplicateAssignmentRow_IsAlsoALostRace()
    {
        var env = new Env();
        env.AddCallCenterAgents(1);
        var ticket = await env.AddQueuedTicketAsync();
        env.UnitOfWork.ThrowDuplicateWriteExceptionOnCall = 1;   // the unique "one current assignment" index

        Assert.Equal(ReconcileOutcome.LostRace, (await env.Reconciler.TryReconcileAsync(ticket.TicketId, Live(), false)).Outcome);
    }

    [Fact]
    public async Task TooNewTickets_AreGivenTimeToBeRoutedByTheirOwnRequest()
    {
        var env = new Env();
        env.AddCallCenterAgents(1);
        var fresh = await env.AddQueuedTicketAsync(e => e with { CreatedAtUtc = Now.AddMinutes(-1) });
        var old = await env.AddQueuedTicketAsync();

        var listed = await env.Reconciler.ListAsync(0, Live());

        Assert.Equal(UnassignedTicketAction.None, listed.Single(l => l.Evidence.TicketId == fresh.TicketId).Classification.Action);
        Assert.Equal(UnassignedTicketAction.RoundRobinCallCenter, listed.Single(l => l.Evidence.TicketId == old.TicketId).Classification.Action);
    }

    [Fact]
    public async Task RoundRobin_IsRestrictedToCsAgents_ADepartmentEmployeeInTheCallCenterNeverReceivesTickets()
    {
        var env = new Env();
        var nonAgent = env.AddNonCsAgentMember();
        var agents = env.AddCallCenterAgents(2);
        var tickets = new List<Ticket>();
        for (var i = 0; i < 6; i++) tickets.Add(await env.AddQueuedTicketAsync());

        foreach (var t in tickets) await env.Reconciler.TryReconcileAsync(t.TicketId, Live(), false);

        Assert.DoesNotContain(tickets, t => t.CurrentOwnerEmployeeId == nonAgent);
        Assert.All(agents, a => Assert.Equal(3, tickets.Count(t => t.CurrentOwnerEmployeeId == a)));
    }

    [Fact]
    public async Task WhenOnlyNonCsAgentsExist_NothingIsAssigned_AndTheTicketIsReported()
    {
        var env = new Env();
        env.AddNonCsAgentMember();
        var ticket = await env.AddQueuedTicketAsync();

        var result = await env.Reconciler.TryReconcileAsync(ticket.TicketId, Live(), false);

        Assert.Equal(ReconcileOutcome.NoEligibleAgent, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task CsAgentRoleOnly_Off_DoesNotReopenTheCallCenterPool_TheDepartmentRuleIsUnconditional()
    {
        // The Call Center deals to CS Agents only (business rule), whatever the reconciler's own option says: switching CsAgentRoleOnly off must
        // never let a Department Employee/Head in the Call Center receive tickets.
        var env = new Env();
        env.AddNonCsAgentMember();
        var ticket = await env.AddQueuedTicketAsync();

        var result = await env.Reconciler.TryReconcileAsync(ticket.TicketId, new CallCenterReconciliationOptions { Enabled = true, DryRun = false, CsAgentRoleOnly = false }, false);

        Assert.Equal(ReconcileOutcome.NoEligibleAgent, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task NoCallCenterConfigured_NothingIsChanged()
    {
        var departments = new FakeDepartmentRepository();
        var leasing = departments.AddDepartment("Leasing", "LCS");
        var tickets = new FakeTicketRepository();
        var uow = new FakeTicketingUnitOfWork();
        var evidence = new StubEvidence();
        var engine = new TicketAutoAssignmentService(
            new FakeRequestTypeAssignmentRuleRepository(), new FakeDepartmentWorkflowSettingsRepository(), new FakeUserDepartmentAssignmentRepository(),
            new FakeTicketAssignmentRepository(), new FakeAuditEntryWriter(), new FakeRotation(), departments);
        var reconciler = new CallCenterUnassignedWorkReconciler(evidence, tickets, engine, uow, new FixedClock());
        var ticket = Ticket.CreateUnclassified("TG-LCS-1", leasing.DepartmentId, "x", Now.AddDays(-1), null, null, defaultPriorityId: null);
        await tickets.AddAsync(ticket);
        evidence.Rows[ticket.TicketId] = new UnassignedTicketEvidence(ticket.TicketId, "TG-LCS-1", TicketStatus.Open, null, leasing.DepartmentId, leasing.DepartmentId,
            Now.AddDays(-1), false, false, false, false, false, false, null, null, false, null, Now.AddDays(-1), false, false);

        var result = await reconciler.TryReconcileAsync(ticket.TicketId, Live(), false);

        Assert.Equal(ReconcileOutcome.NoEligibleAgent, result.Outcome);
        Assert.Equal(0, uow.TransactionsBegun);
    }
}
