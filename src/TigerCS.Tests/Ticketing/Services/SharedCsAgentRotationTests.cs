using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.Administration.Services;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// Business rule: every active human-handled ticket has a CS Agent, picked from ONE shared, persisted round-robin rotation of the
/// Call Center's eligible CS Agents — whatever the ticket's department, channel or trigger. The department and the CS Agent are
/// independent fields. A failure to find an agent is recorded and retryable, never silent.
/// </summary>
public class SharedCsAgentRotationTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);

    private sealed class Env
    {
        public FakeDepartmentRepository Departments { get; } = new();
        public FakeUserDepartmentAssignmentRepository Members { get; } = new();
        public FakeUserAccountManager Accounts { get; } = new();
        public FakeAssignmentRotationRepository Rotation { get; } = new();
        public FakeAuditEntryWriter Audit { get; } = new();
        public FakeTicketAssignmentRepository Assignments { get; } = new();
        public FakeTicketWorkflowEventRepository Events { get; } = new();
        public FakeRequestTypeAssignmentRuleRepository Rules { get; } = new();
        public FakeDepartmentWorkflowSettingsRepository Settings { get; } = new();
        public FakeTicketRepository Tickets { get; } = new();
        public FakeTicketingUnitOfWork UnitOfWork { get; } = new();
        public Department CallCenter { get; }
        public Department Facilities { get; }
        public Department Leasing { get; }
        public TicketAutoAssignmentService Service { get; private set; }
        public TicketAssignmentAppService Assignment { get; private set; }

        public Env(IAssignmentRotationRepository? rotation = null)
        {
            CallCenter = Departments.AddDepartment("Call Center", "CC");
            Facilities = Departments.AddDepartment("Facilities Management", "FM");
            Leasing = Departments.AddDepartment("Leasing", "LEA");
            Service = Build(rotation ?? Rotation);
            Assignment = BuildAssignment();
        }

        public TicketAutoAssignmentService Build(IAssignmentRotationRepository rotation) =>
            new(Rules, Settings, Members, Assignments, Audit, rotation, Departments,
                roleReader: Accounts, accountManager: Accounts, workflowEventRepository: Events);

        private TicketAssignmentAppService BuildAssignment() =>
            new(Tickets, Assignments, Members, Departments, UnitOfWork, Audit, TimeProvider.System, Settings, Service, Events);

        public Guid AddAgent(Department? department = null, string role = Roles.CsAgent)
        {
            var id = Guid.NewGuid();
            Members.Assignments.Add(new UserDepartmentAssignment(id, (department ?? CallCenter).DepartmentId, true, Now, null));
            Accounts.Add(id, id.ToString("N"), role);
            return id;
        }

        /// <summary>Agents in the stable rotation order (ascending id — the order the engine uses).</summary>
        public Guid[] AddAgents(int count) => [.. Enumerable.Range(0, count).Select(_ => AddAgent()).Order()];

        public async Task<Ticket> NewTicketAsync(Department department, int n)
        {
            var ticket = Ticket.CreateUnverified($"TG-CS-20261103-{n:D4}", department.DepartmentId, 5, (byte)PriorityLevel.Medium, "x", Now);
            await Tickets.AddAsync(ticket);
            return ticket;
        }

        public Task<AutoAssignmentResult> EnsureAsync(Ticket t, Guid? preferred = null, AutoAssignmentTrigger trigger = AutoAssignmentTrigger.TicketCreated) =>
            Service.EnsureCsAgentAsync(t, preferred, trigger, Now, Guid.NewGuid());
    }

    // ---- sequential, deterministic, shared ----------------------------------------------------------------------------------------

    [Fact]
    public async Task FourAgents_FiveTickets_AreDealt_1_2_3_4_1_AcrossDepartmentsAndChannels()
    {
        var env = new Env();
        var agents = env.AddAgents(4);
        // Departments deliberately differ: ONE rotation is shared by all of them (and by every channel, which all end here).
        var departments = new[] { env.Facilities, env.Leasing, env.CallCenter, env.Facilities, env.Leasing };

        var owners = new List<Guid?>();
        for (var i = 0; i < departments.Length; i++)
        {
            var ticket = await env.NewTicketAsync(departments[i], i + 1);
            var result = await env.EnsureAsync(ticket);
            Assert.Equal(AutoAssignmentOutcome.AssignedRoundRobin, result.Outcome);
            Assert.Equal(departments[i].DepartmentId, ticket.CurrentDepartmentId);   // department unaffected by the agent
            owners.Add(ticket.CurrentOwnerEmployeeId);
        }

        Assert.Equal([agents[0], agents[1], agents[2], agents[3], agents[0]], owners);
        Assert.Equal([env.CallCenter.DepartmentId], env.Rotation.Rows.Keys);          // a single cursor, not one per department
        Assert.Equal(5, env.Assignments.Added.Count(a => a.IsCurrent));
    }

    [Fact]
    public async Task UnavailableAgents_AreSkipped_LockedOut_NonCsAgent_ReadOnly_AndOtherDepartmentsStaff()
    {
        var env = new Env();
        var agents = env.AddAgents(3);
        env.Accounts.LockOut(agents[1]);                                     // existing availability rule: locked out
        env.AddAgent(role: Roles.DepartmentEmployee);                        // Call Center member without the CS Agent role
        env.AddAgent(role: Roles.CsSupervisor);                              // never dealt work by rotation
        env.AddAgent(env.Facilities, Roles.CsAgent);                         // a CS Agent who is NOT in the Call Center pool

        var owners = new List<Guid?>();
        for (var i = 1; i <= 4; i++)
        {
            var t = await env.NewTicketAsync(env.Leasing, i);
            await env.EnsureAsync(t);
            owners.Add(t.CurrentOwnerEmployeeId);
        }

        Assert.Equal([agents[0], agents[2], agents[0], agents[2]], owners);
    }

    [Fact]
    public async Task Rotation_SurvivesAnApplicationRestart_BecauseTheCursorIsPersisted()
    {
        var env = new Env();
        var agents = env.AddAgents(3);

        var first = await env.NewTicketAsync(env.Facilities, 1);
        await env.EnsureAsync(first);

        // "Restart": a brand-new service instance over the same persisted cursor store.
        var restarted = env.Build(env.Rotation);
        var second = await env.NewTicketAsync(env.Leasing, 2);
        await restarted.EnsureCsAgentAsync(second, null, AutoAssignmentTrigger.TicketCreated, Now, Guid.NewGuid());
        var third = await env.NewTicketAsync(env.Leasing, 3);
        await restarted.EnsureCsAgentAsync(third, null, AutoAssignmentTrigger.TicketCreated, Now, Guid.NewGuid());

        Assert.Equal([agents[0], agents[1], agents[2]], new[] { first, second, third }.Select(t => t.CurrentOwnerEmployeeId!.Value));
    }

    /// <summary>A rotation store that behaves like the real row lock: the cursor is exclusive from read until the caller's "transaction" ends.</summary>
    private sealed class LockingRotation : IAssignmentRotationRepository
    {
        private readonly SemaphoreSlim _rowLock = new(1, 1);
        private readonly Dictionary<int, DepartmentAssignmentRotation> _rows = [];

        public async Task<DepartmentAssignmentRotation> GetForUpdateAsync(int departmentId, DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            await _rowLock.WaitAsync(cancellationToken);
            await Task.Yield();   // give a racing caller every chance to interleave if the engine did not wait for the lock
            if (!_rows.TryGetValue(departmentId, out var row))
            {
                _rows[departmentId] = row = new DepartmentAssignmentRotation(departmentId, nowUtc);
            }

            return row;
        }

        public void EndTransaction() => _rowLock.Release();
    }

    [Fact]
    public async Task ConcurrentCreation_NeverAssignsTheSameRotationPositionTwice()
    {
        var locking = new LockingRotation();
        var env = new Env(locking);
        var agents = env.AddAgents(4);
        var service = env.Service;
        var completionOrder = new List<Guid>();
        var tickets = new List<Ticket>();
        for (var i = 1; i <= 20; i++)
        {
            tickets.Add(Ticket.CreateUnverified($"TG-CS-20261103-{i:D4}", i % 2 == 0 ? env.Facilities.DepartmentId : env.Leasing.DepartmentId, 5,
                (byte)PriorityLevel.Medium, "x", Now));
        }

        await Task.WhenAll(tickets.Select(t => Task.Run(async () =>
        {
            // Each task is one creation transaction: the engine takes the cursor lock, picks, advances; the lock ends with the "commit".
            var result = await service.EnsureCsAgentAsync(t, null, AutoAssignmentTrigger.TicketCreated, Now, Guid.NewGuid());
            lock (completionOrder)
            {
                completionOrder.Add(result.AssignedEmployeeId!.Value);
            }

            locking.EndTransaction();
        })));

        Assert.Equal(20, completionOrder.Count);
        Assert.All(agents, a => Assert.Equal(5, completionOrder.Count(o => o == a)));                    // perfectly even
        for (var i = 0; i + 4 <= completionOrder.Count; i += 4)
        {
            Assert.Equal(4, completionOrder.Skip(i).Take(4).Distinct().Count());                         // no position handed out twice per cycle
        }
    }

    // ---- ownership is never disturbed, closed/AI-closed tickets need nothing ------------------------------------------------------

    [Fact]
    public async Task AnAlreadyAssignedTicket_IsLeftAlone_NoDuplicateAssignmentOrAuditEvent()
    {
        var env = new Env();
        env.AddAgents(2);
        var ticket = await env.NewTicketAsync(env.Facilities, 1);
        var owner = Guid.NewGuid();
        ticket.AssignTo(owner);

        var result = await env.EnsureAsync(ticket);

        Assert.Equal(AutoAssignmentOutcome.AlreadyAssigned, result.Outcome);
        Assert.Equal(owner, ticket.CurrentOwnerEmployeeId);
        Assert.Empty(env.Assignments.Added);
        Assert.Empty(env.Audit.Entries);
        Assert.Empty(env.Rotation.Rows);
    }

    [Fact]
    public async Task ATicketClosedOrResolvedByTheAi_NeedsNoCsAgent_AndNothingIsWritten()
    {
        var env = new Env();
        env.AddAgents(2);
        var resolved = await env.NewTicketAsync(env.Facilities, 1);
        resolved.AssignTo(Guid.NewGuid());
        resolved.ChangeStatus(TicketStatus.InProgress);
        resolved.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        var closed = await env.NewTicketAsync(env.Leasing, 2);
        closed.AssignTo(Guid.NewGuid());
        closed.ChangeStatus(TicketStatus.InProgress);
        closed.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        closed.Close();

        foreach (var ticket in new[] { resolved, closed })
        {
            var result = await env.EnsureAsync(ticket);
            Assert.Equal(AutoAssignmentOutcome.NotApplicable, result.Outcome);
        }

        Assert.Empty(env.Assignments.Added);
        Assert.Empty(env.Audit.Entries);
        Assert.Empty(env.Events.All);
    }

    // ---- transfer: preserve unless explicitly reassigned --------------------------------------------------------------------------

    [Fact]
    public async Task Transfer_OfATicketWithoutAnAgent_GetsOneFromTheSharedRotation_EvenWhenTheDestinationHasNoStaffAndAQueueRule()
    {
        var env = new Env();
        var agents = env.AddAgents(2);
        var ticket = await env.NewTicketAsync(env.Leasing, 1);       // legacy / failed ticket: no agent
        ticket.ClassifyRequestType(10);
        env.Rules.Add(RequestTypeAssignmentRule.ForDepartmentQueue(10));   // "Facilities Management Queue (Unassigned)" before this fix

        var result = await env.Assignment.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId, new TransferTicketRequestDto(env.Facilities.DepartmentId, "Wrong team", []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(env.Facilities.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(agents[0], ticket.CurrentOwnerEmployeeId);
        Assert.Single(env.Assignments.Added, a => a.IsCurrent && a.AssignedEmployeeId == agents[0]);
    }

    [Fact]
    public async Task Transfer_PreservesTheCsAgent_AndAnExplicitReassignmentReplacesIt()
    {
        var env = new Env();
        var agents = env.AddAgents(2);
        var ticket = await env.NewTicketAsync(env.Leasing, 1);
        await env.EnsureAsync(ticket);
        Assert.Equal(agents[0], ticket.CurrentOwnerEmployeeId);
        var assignmentsBefore = env.Assignments.Added.Count;

        var kept = await env.Assignment.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId, new TransferTicketRequestDto(env.Facilities.DepartmentId, "FM issue", []));
        Assert.Equal(TicketMutationOutcome.Success, kept.Outcome);
        Assert.Equal(agents[0], ticket.CurrentOwnerEmployeeId);
        Assert.Equal(assignmentsBefore, env.Assignments.Added.Count);       // no duplicate assignment row

        var facilitiesStaff = env.AddAgent(env.Leasing, Roles.DepartmentEmployee);
        var reassigned = await env.Assignment.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(env.Leasing.DepartmentId, "Back to Leasing", [], facilitiesStaff));
        Assert.Equal(TicketMutationOutcome.Success, reassigned.Outcome);
        Assert.Equal(facilitiesStaff, ticket.CurrentOwnerEmployeeId);
        Assert.Single(env.Assignments.Added, a => a.IsCurrent);              // still exactly one current owner
    }

    // ---- reopen support -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RestorePreviousCsAgent_ReturnsTheSameAgentWhenStillEligible_OtherwiseNullSoTheRotationDecides()
    {
        var env = new Env();
        var agents = env.AddAgents(2);

        var ticket = await env.NewTicketAsync(env.Facilities, 1);
        var restored = await env.Service.RestorePreviousCsAgentAsync(ticket, agents[1], AutoAssignmentTrigger.DepartmentTransfer, Now, Guid.NewGuid());
        Assert.Equal(AutoAssignmentOutcome.PreservedCsAgent, restored!.Outcome);
        Assert.Equal(agents[1], ticket.CurrentOwnerEmployeeId);
        Assert.Contains(env.Audit.Entries, e => e.Action == "AutoAssign" && e.AfterValue!.Contains("Mode=PreservedCsAgent"));

        env.Accounts.LockOut(agents[0]);
        var other = await env.NewTicketAsync(env.Facilities, 2);
        Assert.Null(await env.Service.RestorePreviousCsAgentAsync(other, agents[0], AutoAssignmentTrigger.DepartmentTransfer, Now, Guid.NewGuid()));
        Assert.Null(other.CurrentOwnerEmployeeId);
        Assert.Null(await env.Service.RestorePreviousCsAgentAsync(other, null, AutoAssignmentTrigger.DepartmentTransfer, Now, Guid.NewGuid()));
    }

    // ---- failure handling + safe retry --------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoEligibleAgent_RecordsTheFailureOnce_ExposesItToStaff_AndLeavesTheTicketUnassignedWithoutGuessing()
    {
        var env = new Env();
        env.AddAgent(role: Roles.DepartmentEmployee);                          // not a CS Agent
        env.AddAgent(env.Facilities, Roles.DepartmentEmployee);                // department staff are never a guess
        var ticket = await env.NewTicketAsync(env.Facilities, 1);

        var first = await env.EnsureAsync(ticket);
        var again = await env.EnsureAsync(ticket);                             // a reconciler / second path hitting the same ticket

        Assert.Equal(AutoAssignmentOutcome.NoEligibleEmployee, first.Outcome);
        Assert.Equal(AutoAssignmentOutcome.NoEligibleEmployee, again.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Empty(env.Assignments.Added);
        var failure = Assert.Single(env.Events.All, e => e.EventType == WorkflowEventType.ManualAssignmentRequired);   // one event, not one per attempt
        Assert.Contains(TicketAutoAssignmentService.AssignmentFailureMarker, failure.Note);
        Assert.Contains(env.Audit.Entries, e => e.Action == "AutoAssign" && e.AfterValue!.Contains("Status=Unassigned"));
    }

    [Fact]
    public async Task NoCallCenterConfigured_IsAnAssignmentFailure_NotASilentQueue()
    {
        var env = new Env();
        env.Departments.AddDepartment("Collections", "COL");
        var emptyDepartments = new FakeDepartmentRepository();
        var dept = emptyDepartments.AddDepartment("Leasing", "LEA");
        var service = new TicketAutoAssignmentService(
            env.Rules, env.Settings, env.Members, env.Assignments, env.Audit, env.Rotation, emptyDepartments,
            roleReader: env.Accounts, accountManager: env.Accounts, workflowEventRepository: env.Events);
        var ticket = Ticket.CreateUnverified("TG-CS-20261103-0001", dept.DepartmentId, 5, (byte)PriorityLevel.Medium, "x", Now);

        var result = await service.EnsureCsAgentAsync(ticket, null, AutoAssignmentTrigger.TicketCreated, Now, Guid.NewGuid());

        Assert.Equal(AutoAssignmentOutcome.NoEligibleEmployee, result.Outcome);
        Assert.Contains(env.Events.All, e => e.EventType == WorkflowEventType.ManualAssignmentRequired);
    }

    [Fact]
    public async Task Retry_AssignsOnceAnAgentExists_IsIdempotent_AndIsRestrictedToManagers()
    {
        var env = new Env();
        var ticket = await env.NewTicketAsync(env.Facilities, 1);
        await env.EnsureAsync(ticket);                                          // fails: nobody eligible
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        var manager = Guid.NewGuid();

        var stillNobody = await env.Assignment.RetryCsAgentAssignmentAsync(manager, [Roles.CsManager], ticket.TicketId);
        Assert.Equal(TicketMutationOutcome.NoEligibleCsAgent, stillNobody.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);

        Assert.Equal(TicketMutationOutcome.Forbidden,
            (await env.Assignment.RetryCsAgentAssignmentAsync(Guid.NewGuid(), [Roles.CsAgent], ticket.TicketId)).Outcome);
        Assert.Equal(TicketMutationOutcome.NotFound, (await env.Assignment.RetryCsAgentAssignmentAsync(manager, [Roles.CsManager], 9999)).Outcome);

        var agent = env.AddAgent();
        var retried = await env.Assignment.RetryCsAgentAssignmentAsync(manager, [Roles.CsManager], ticket.TicketId);
        Assert.Equal(TicketMutationOutcome.Success, retried.Outcome);
        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(env.Facilities.DepartmentId, ticket.CurrentDepartmentId);

        var auditAfterFirst = env.Audit.Entries.Count;
        var repeated = await env.Assignment.RetryCsAgentAssignmentAsync(manager, [Roles.CsManager], ticket.TicketId);   // double click / second manager
        Assert.Equal(TicketMutationOutcome.Success, repeated.Outcome);
        Assert.Single(env.Assignments.Added, a => a.IsCurrent);
        Assert.Equal(auditAfterFirst, env.Audit.Entries.Count);                                                          // nothing duplicated
    }

    [Fact]
    public async Task Retry_OfAClosedTicket_IsRefused()
    {
        var env = new Env();
        env.AddAgents(1);
        var ticket = await env.NewTicketAsync(env.Facilities, 1);
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        ticket.Close();

        var result = await env.Assignment.RetryCsAgentAssignmentAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId);

        Assert.Equal(TicketMutationOutcome.TicketClosed, result.Outcome);
    }
}
