using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Infrastructure;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.Administration.Services;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// Department transfer without a named assignee: the ticket is dealt IMMEDIATELY, inside the transfer's own transaction, to the next eligible
/// active employee of the DESTINATION department by the existing Round Robin engine (same persisted, row-locked cursor, same eligibility
/// filter, same assignment row / audit / notification as a configured Round Robin rule - no separate algorithm). A named assignee is a manual
/// assignment and is never overridden; when nobody is eligible the ticket waits unassigned in the destination queue with the reason recorded.
/// </summary>
public class TransferRoundRobinAssignmentTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Manager = [Roles.CsManager];

    // ---- harness ------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A transaction scope that follows the unit of work: the rotation cursor's row lock (<c>GetForUpdateAsync</c>) is held until the
    /// transaction commits or rolls back, and a rollback restores the cursor - the two properties the real SQL Server repository provides
    /// (<c>UPDLOCK</c> row lock + transactional write). Set synchronously in <c>BeginTransactionAsync</c> so it flows into the caller only.
    /// </summary>
    private sealed class TransactionScope
    {
        public List<Action<bool>> OnEnd { get; } = [];

        public void End(bool committed)
        {
            foreach (var action in OnEnd.AsEnumerable().Reverse())
            {
                action(committed);
            }

            OnEnd.Clear();
        }
    }

    private static readonly AsyncLocal<TransactionScope?> CurrentScope = new();

    private sealed class ScopedUnitOfWork(FakeTicketingUnitOfWork inner) : ITicketingUnitOfWork
    {
        public Task SaveChangesAsync(CancellationToken cancellationToken = default) => inner.SaveChangesAsync(cancellationToken);

        // Deliberately NOT async: the scope must be set in the CALLER's execution context.
        public Task<ITicketingTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            var scope = new TransactionScope();
            CurrentScope.Value = scope;
            var transaction = inner.BeginTransactionAsync(cancellationToken).GetAwaiter().GetResult();
            return Task.FromResult<ITicketingTransaction>(new ScopedTransaction(transaction, scope));
        }
    }

    private sealed class ScopedTransaction(ITicketingTransaction inner, TransactionScope scope) : ITicketingTransaction
    {
        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await inner.CommitAsync(cancellationToken);
            scope.End(committed: true);
        }

        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            await inner.RollbackAsync(cancellationToken);
            scope.End(committed: false);
        }

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            scope.End(committed: false);    // no-op when already committed / rolled back
        }
    }

    /// <summary>In-memory <see cref="IAssignmentRotationRepository"/> with a per-department lock held to the end of the caller's transaction.</summary>
    private sealed class LockingRotationRepository : IAssignmentRotationRepository
    {
        private readonly Dictionary<int, SemaphoreSlim> _locks = [];

        public Dictionary<int, DepartmentAssignmentRotation> Rows { get; } = [];

        /// <summary>Test hook, awaited while the department's cursor lock is held (to park a transaction mid-flight).</summary>
        public Func<int, Task>? WhileLocked { get; set; }

        public async Task<DepartmentAssignmentRotation> GetForUpdateAsync(int departmentId, DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            var scope = CurrentScope.Value ?? throw new InvalidOperationException("The cursor must be locked inside a transaction.");
            SemaphoreSlim gate;
            lock (_locks)
            {
                if (!_locks.TryGetValue(departmentId, out gate!))
                {
                    _locks[departmentId] = gate = new SemaphoreSlim(1, 1);
                }
            }

            await gate.WaitAsync(cancellationToken);
            if (!Rows.TryGetValue(departmentId, out var row))
            {
                Rows[departmentId] = row = new DepartmentAssignmentRotation(departmentId, nowUtc);
            }

            var before = row.LastAssignedEmployeeId;
            scope.OnEnd.Add(committed =>
            {
                if (!committed)
                {
                    typeof(DepartmentAssignmentRotation).GetProperty(nameof(DepartmentAssignmentRotation.LastAssignedEmployeeId))!.SetValue(row, before);
                }

                gate.Release();
            });

            if (WhileLocked is { } hook)
            {
                await hook(departmentId);
            }

            return row;
        }
    }

    private sealed class Env
    {
        public FakeDepartmentRepository Departments { get; } = new();
        public FakeUserDepartmentAssignmentRepository Members { get; } = new();
        public FakeUserAccountManager Accounts { get; } = new();
        public LockingRotationRepository Rotation { get; } = new();
        public FakeAuditEntryWriter Audit { get; } = new();
        public FakeTicketAssignmentRepository Assignments { get; } = new();
        public FakeTicketWorkflowEventRepository Events { get; } = new();
        public FakeRequestTypeAssignmentRuleRepository Rules { get; } = new();
        public FakeDepartmentWorkflowSettingsRepository Settings { get; } = new();
        public FakeTicketRepository Tickets { get; } = new();
        public FakeTicketingUnitOfWork InnerUnitOfWork { get; } = new();
        public FakeOutboxWriter Outbox { get; } = new();
        public Department Source { get; }
        public Department Destination { get; }
        public Department Other { get; }
        public TicketAssignmentAppService Assignment { get; }

        public Env()
        {
            Source = Departments.AddDepartment("Leasing", "LEA");
            Destination = Departments.AddDepartment("Facilities Management", "FM");
            Other = Departments.AddDepartment("Maintenance", "MNT");
            InnerUnitOfWork.OutboxWriter = Outbox;

            var service = new TicketAutoAssignmentService(
                Rules, Settings, Members, Assignments, Audit, Rotation, Departments,
                roleReader: Accounts, accountManager: Accounts, workflowEventRepository: Events, outboxWriter: Outbox);
            Assignment = new TicketAssignmentAppService(
                Tickets, Assignments, Members, Departments, new ScopedUnitOfWork(InnerUnitOfWork), Audit, TimeProvider.System, Settings, service, Events,
                outboxWriter: Outbox);
        }

        /// <summary>Adds an operational employee to the department. Employees are dealt in ascending id order, so tests sort what this returns.</summary>
        public Guid AddStaff(Department department, string role = Roles.DepartmentEmployee)
        {
            var id = Guid.NewGuid();
            Members.Assignments.Add(new UserDepartmentAssignment(id, department.DepartmentId, true, Now, null));
            Accounts.Add(id, id.ToString("N"), role);
            return id;
        }

        public Guid[] AddStaff(Department department, int count) => [.. Enumerable.Range(0, count).Select(_ => AddStaff(department)).Order()];

        /// <summary>A ticket in <paramref name="department"/> owned by a previous employee of that department (with its current assignment row).</summary>
        public async Task<(Ticket Ticket, Guid PreviousOwner)> OwnedTicketAsync(Department department, int n)
        {
            var previous = AddStaff(department);
            var ticket = Ticket.CreateUnverified($"TG-CS-20261103-{n:D4}", department.DepartmentId, 5, (byte)PriorityLevel.High, "x", Now);
            await Tickets.AddAsync(ticket);
            ticket.AssignTo(previous);
            await Assignments.AddAsync(new TicketAssignment(ticket.TicketId, previous, department.DepartmentId, Now, assigningActorEmployeeId: null));
            return (ticket, previous);
        }

        public Task<TicketMutationResult> TransferAsync(Ticket ticket, Department to, Guid? assignTo = null, string[]? roles = null) =>
            Assignment.TransferAsync(
                Guid.NewGuid(), roles ?? Manager, ticket.TicketId, new TransferTicketRequestDto(to.DepartmentId, "Belongs to the other team", [], assignTo));

        public IEnumerable<TicketAssignment> CurrentRows(Ticket ticket) => Assignments.Added.Where(a => a.TicketId == ticket.TicketId && a.IsCurrent);
    }

    // ---- 1. automatic assignment on transfer -------------------------------------------------------------------------------------

    [Fact]
    public async Task Transfer_WithoutAnAssignee_DealsTheNextEligibleEmployeeOfTheDestination_ImmediatelyInsideTheTransferTransaction()
    {
        var env = new Env();
        var staff = env.AddStaff(env.Destination, 3);
        var (ticket, previous) = await env.OwnedTicketAsync(env.Source, 1);
        var (priority, status) = (ticket.PriorityId, ticket.TicketStatus);

        var result = await env.TransferAsync(ticket, env.Destination);

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(env.Destination.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(staff[0], ticket.CurrentOwnerEmployeeId);                                      // first in the destination's order
        Assert.Equal(staff[0], env.Rotation.Rows[env.Destination.DepartmentId].LastAssignedEmployeeId);   // cursor advanced in the same operation
        Assert.DoesNotContain(env.Source.DepartmentId, env.Rotation.Rows.Keys);                     // never the source department's cursor

        // One transaction, one save: nothing was left for a background job.
        Assert.Equal(1, env.InnerUnitOfWork.TransactionsBegun);
        Assert.Equal(1, env.InnerUnitOfWork.TransactionsCommitted);
        Assert.Equal(1, env.InnerUnitOfWork.SaveChangesCallCount);

        // Assignment history: the previous row is superseded, exactly one current row exists, in the destination, written by the system.
        Assert.Contains(env.Assignments.Added, a => a.AssignedEmployeeId == previous && !a.IsCurrent);
        var current = Assert.Single(env.CurrentRows(ticket));
        Assert.Equal(staff[0], current.AssignedEmployeeId);
        Assert.Equal(env.Destination.DepartmentId, current.AssignedDepartmentId);
        Assert.Null(current.AssigningActorEmployeeId);

        // Status, priority and the rest of the ticket are untouched (SLA is driven by them).
        Assert.Equal(priority, ticket.PriorityId);
        Assert.Equal(status, ticket.TicketStatus);
    }

    [Fact]
    public async Task Transfer_RecordsTransferHistory_AuditLogs_AndNotifiesTheNewAssignee_UnderOneCorrelationId()
    {
        var env = new Env();
        var staff = env.AddStaff(env.Destination, 2);
        var (ticket, previous) = await env.OwnedTicketAsync(env.Source, 1);
        var manager = Guid.NewGuid();

        var result = await env.Assignment.TransferAsync(
            manager, Manager, ticket.TicketId,
            new TransferTicketRequestDto(env.Destination.DepartmentId, "Needs Facilities", [], null, TicketTransferReason.WrongDepartment));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);

        // Transfer history: who moved it, where to, who took it, why.
        var transfer = Assert.Single(env.Events.All, e => e.EventType == WorkflowEventType.TicketTransferred);
        Assert.Equal(manager, transfer.ActorEmployeeId);
        Assert.Equal(env.Source.DepartmentId, transfer.FromDepartmentId);
        Assert.Equal(env.Destination.DepartmentId, transfer.ToDepartmentId);
        Assert.Equal(staff[0], transfer.ToEmployeeId);
        Assert.Equal((byte)TicketTransferReason.WrongDepartment, transfer.TransferReasonCode);

        // Audit: the transfer by the manager and the automatic assignment by the system, tied by the same correlation id.
        var transferAudit = Assert.Single(env.Audit.Entries, e => e.Action == "Transfer");
        var autoAudit = Assert.Single(env.Audit.Entries, e => e.Action == "AutoAssign");
        Assert.Equal(manager, transferAudit.ActorEmployeeId);
        Assert.Null(autoAudit.ActorEmployeeId);
        Assert.Equal(previous.ToString(), autoAudit.BeforeValue);
        Assert.Contains($"AssignedEmployeeId={staff[0]}", autoAudit.AfterValue);
        Assert.Contains("Mode=RoundRobin", autoAudit.AfterValue);
        Assert.Contains("Trigger=DepartmentTransfer", autoAudit.AfterValue);
        Assert.Equal(transferAudit.CorrelationId, autoAudit.CorrelationId);
        Assert.Equal(transfer.CorrelationId, transferAudit.CorrelationId);

        // Notification: the employee who received it is told, in the same committed transaction.
        Assert.Contains(env.Outbox.Committed, m => m.EventType == OutboxEventTypes.TicketAssignedToAgent && m.Payload.Contains(staff[0].ToString()));
        Assert.DoesNotContain(env.Outbox.Staged, m => !env.Outbox.Committed.Contains(m));
    }

    [Fact]
    public async Task Transfer_SkipsIneligibleEmployees_LockedOut_ReadOnlyRole_AndEmployeesOfOtherDepartments()
    {
        var env = new Env();
        var staff = env.AddStaff(env.Destination, 3);
        env.Accounts.LockOut(staff[0]);                                          // existing availability rule: locked out
        env.AddStaff(env.Destination, Roles.ChairmanCeo);                        // read-only role: never dealt work
        env.AddStaff(env.Other);                                                 // eligible, but in another department

        var assigned = new List<Guid?>();
        for (var i = 1; i <= 4; i++)
        {
            var (ticket, _) = await env.OwnedTicketAsync(env.Source, i);
            Assert.Equal(TicketMutationOutcome.Success, (await env.TransferAsync(ticket, env.Destination)).Outcome);
            assigned.Add(ticket.CurrentOwnerEmployeeId);
        }

        Assert.Equal([staff[1], staff[2], staff[1], staff[2]], assigned);
    }

    [Fact]
    public async Task Transfer_OfAnUnclassifiedTicket_AndOfOneWhoseRuleSaysDepartmentQueue_StillGetsTheDestinationsRoundRobin()
    {
        var env = new Env();
        var staff = env.AddStaff(env.Destination, 2);
        var (unclassified, _) = await env.OwnedTicketAsync(env.Source, 1);            // no request type: no rule can apply
        var (queued, _) = await env.OwnedTicketAsync(env.Source, 2);
        queued.ClassifyRequestType(10);
        env.Rules.Add(RequestTypeAssignmentRule.ForDepartmentQueue(10));

        await env.TransferAsync(unclassified, env.Destination);
        await env.TransferAsync(queued, env.Destination);

        Assert.Equal(staff[0], unclassified.CurrentOwnerEmployeeId);
        Assert.Equal(staff[1], queued.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task Transfer_UsesTheSameRoundRobinCursorAsAConfiguredRoundRobinRule()
    {
        var env = new Env();
        var staff = env.AddStaff(env.Destination, 3);
        var (ruled, _) = await env.OwnedTicketAsync(env.Source, 1);
        ruled.ClassifyRequestType(10);
        env.Rules.Add(RequestTypeAssignmentRule.ForRoundRobin(10));
        var (plain, _) = await env.OwnedTicketAsync(env.Source, 2);

        await env.TransferAsync(ruled, env.Destination);        // dealt by the configured Round Robin rule
        await env.TransferAsync(plain, env.Destination);        // dealt by the transfer fallback: continues from the same cursor

        Assert.Equal(staff[0], ruled.CurrentOwnerEmployeeId);
        Assert.Equal(staff[1], plain.CurrentOwnerEmployeeId);
        Assert.Single(env.Rotation.Rows);                       // ONE cursor for the department, not a second algorithm's state
    }

    // ---- 2. manual assignment is preserved -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Transfer_WithANamedEmployee_IsAManualAssignment_NeverOverriddenAndNeverAdvancingTheRotation()
    {
        var env = new Env();
        var staff = env.AddStaff(env.Destination, 3);
        var chosen = staff[2];                                                    // deliberately NOT the next one in the rotation
        var (ticket, previous) = await env.OwnedTicketAsync(env.Source, 1);

        var result = await env.TransferAsync(ticket, env.Destination, assignTo: chosen);

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(chosen, ticket.CurrentOwnerEmployeeId);
        Assert.Empty(env.Rotation.Rows);                                          // the cursor was neither consulted nor advanced
        Assert.DoesNotContain(env.Audit.Entries, e => e.Action == "AutoAssign");
        Assert.Contains(env.Audit.Entries, e => e.Action == "Assign" && e.AfterValue!.Contains($"{chosen};") && e.AfterValue.Contains("ViaTransfer=true"));
        Assert.Equal(chosen, Assert.Single(env.CurrentRows(ticket)).AssignedEmployeeId);
        Assert.Contains(env.Assignments.Added, a => a.AssignedEmployeeId == previous && !a.IsCurrent);
        Assert.Equal(chosen, Assert.Single(env.Events.All, e => e.EventType == WorkflowEventType.TicketTransferred).ToEmployeeId);
        Assert.Contains(env.Outbox.Committed, m => m.EventType == OutboxEventTypes.TicketAssignedToAgent && m.Payload.Contains(chosen.ToString()));

        // The next AUTOMATIC transfer then starts from the beginning: the manual choice did not consume a slot.
        var (next, _) = await env.OwnedTicketAsync(env.Source, 2);
        await env.TransferAsync(next, env.Destination);
        Assert.Equal(staff[0], next.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task Transfer_WithANamedEmployeeOutsideTheDestination_IsRefused_AndChangesNothing()
    {
        var env = new Env();
        env.AddStaff(env.Destination, 2);
        var outsider = env.AddStaff(env.Other);
        var (ticket, previous) = await env.OwnedTicketAsync(env.Source, 1);

        var result = await env.TransferAsync(ticket, env.Destination, assignTo: outsider);

        Assert.Equal(TicketMutationOutcome.EmployeeNotInDepartment, result.Outcome);
        Assert.Equal(env.Source.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(previous, ticket.CurrentOwnerEmployeeId);
        Assert.Empty(env.Rotation.Rows);
        Assert.Equal(0, env.InnerUnitOfWork.TransactionsBegun);
    }

    // ---- 3. no eligible employee ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Transfer_WhenNoOneInTheDestinationIsEligible_KeepsTheTicketUnassignedThere_AndRecordsTheReason()
    {
        var env = new Env();
        var lockedOut = env.AddStaff(env.Destination);
        env.Accounts.LockOut(lockedOut);
        var (ticket, previous) = await env.OwnedTicketAsync(env.Source, 1);
        var manager = Guid.NewGuid();

        var result = await env.Assignment.TransferAsync(
            manager, Manager, ticket.TicketId, new TransferTicketRequestDto(env.Destination.DepartmentId, "Needs Facilities", []));

        // The transfer itself succeeded...
        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(env.Destination.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(1, env.InnerUnitOfWork.TransactionsCommitted);

        // ...and the ticket waits UNASSIGNED in the destination queue: the previous department's employee does not stay accountable.
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Empty(env.CurrentRows(ticket));
        Assert.Contains(env.Assignments.Added, a => a.AssignedEmployeeId == previous && !a.IsCurrent);
        Assert.DoesNotContain(env.Rotation.Rows.Values, r => r.LastAssignedEmployeeId is not null);

        // The reason is on record: audited, flagged for staff, and shown on the transfer's own history.
        Assert.Contains(env.Audit.Entries, e => e.Action == "AutoAssign" && e.AfterValue!.StartsWith("DepartmentQueue") && e.AfterValue.Contains("Trigger=DepartmentTransfer"));
        Assert.Contains(env.Audit.Entries, e =>
            e.Action == "Unassign" && e.BeforeValue == previous.ToString() && e.AfterValue!.Contains("Status=Unassigned") && e.AfterValue.Contains("NoEligibleEmployee"));
        Assert.Contains(env.Events.All, e =>
            e.EventType == WorkflowEventType.ManualAssignmentRequired && e.Note!.Contains(TicketAutoAssignmentService.AssignmentFailureMarker));
        Assert.Null(Assert.Single(env.Events.All, e => e.EventType == WorkflowEventType.TicketTransferred).ToEmployeeId);
        Assert.DoesNotContain(env.Outbox.Committed, m => m.EventType == OutboxEventTypes.TicketAssignedToAgent);
    }

    [Fact]
    public async Task Transfer_IntoADepartmentWhoseSettingsDisableAssignment_LeavesItUnassigned_WithoutTouchingTheCursor()
    {
        var env = new Env();
        env.AddStaff(env.Destination, 2);
        env.Settings.Add(new DepartmentWorkflowSettings(env.Destination.DepartmentId, allowAssignment: false, allowInternalReassignment: true, allowTransferToOtherDepartments: true));
        var (ticket, _) = await env.OwnedTicketAsync(env.Source, 1);

        var result = await env.TransferAsync(ticket, env.Destination);

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Empty(env.CurrentRows(ticket));
        Assert.Empty(env.Rotation.Rows);
        Assert.Contains(env.Events.All, e => e.EventType == WorkflowEventType.ManualAssignmentRequired);
    }

    [Fact]
    public async Task Transfer_OfAnAlreadyUnassignedTicket_WithNoEligibleEmployee_RecordsTheFailureOnce_AndAnEligibleEmployeeLaterGetsTheNextTransfer()
    {
        var env = new Env();
        var ticket = Ticket.CreateUnverified("TG-CS-20261103-0001", env.Source.DepartmentId, 5, (byte)PriorityLevel.High, "x", Now);
        await env.Tickets.AddAsync(ticket);

        Assert.Equal(TicketMutationOutcome.Success, (await env.TransferAsync(ticket, env.Destination)).Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(TicketMutationOutcome.Success, (await env.TransferAsync(ticket, env.Other)).Outcome);   // second failure on the same ticket
        Assert.Single(env.Events.All, e => e.EventType == WorkflowEventType.ManualAssignmentRequired);       // flagged once, never spammed

        var late = env.AddStaff(env.Destination);
        Assert.Equal(TicketMutationOutcome.Success, (await env.TransferAsync(ticket, env.Destination)).Outcome);
        Assert.Equal(late, ticket.CurrentOwnerEmployeeId);
    }

    // ---- 4. consecutive transfers ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConsecutiveTransfers_AreDealtInRoundRobinOrder_AndWrapAround()
    {
        var env = new Env();
        var staff = env.AddStaff(env.Destination, 3);

        var assigned = new List<Guid?>();
        for (var i = 1; i <= 7; i++)
        {
            var (ticket, _) = await env.OwnedTicketAsync(env.Source, i);
            await env.TransferAsync(ticket, env.Destination);
            assigned.Add(ticket.CurrentOwnerEmployeeId);
        }

        Assert.Equal([staff[0], staff[1], staff[2], staff[0], staff[1], staff[2], staff[0]], assigned);
        Assert.Equal(staff[0], env.Rotation.Rows[env.Destination.DepartmentId].LastAssignedEmployeeId);
    }

    [Fact]
    public async Task ConsecutiveTransfers_KeepOneCursorPerDestination_AndTheSameTicketTransferredOnwardIsRedealtByTheNewDepartment()
    {
        var env = new Env();
        var facilities = env.AddStaff(env.Destination, 2);
        var maintenance = env.AddStaff(env.Other, 2);
        var (a, _) = await env.OwnedTicketAsync(env.Source, 1);
        var (b, _) = await env.OwnedTicketAsync(env.Source, 2);

        await env.TransferAsync(a, env.Destination);      // Facilities: [0]
        await env.TransferAsync(b, env.Other);            // Maintenance: [0] - its own cursor, unaffected by Facilities
        Assert.Equal(facilities[0], a.CurrentOwnerEmployeeId);
        Assert.Equal(maintenance[0], b.CurrentOwnerEmployeeId);

        await env.TransferAsync(a, env.Other);            // the same ticket moves on: Maintenance's NEXT employee takes it
        Assert.Equal(maintenance[1], a.CurrentOwnerEmployeeId);
        Assert.Equal(env.Other.DepartmentId, a.CurrentDepartmentId);

        // Exactly one current assignment per ticket throughout, and the earlier owner's row was superseded.
        Assert.Equal(maintenance[1], Assert.Single(env.CurrentRows(a)).AssignedEmployeeId);
        Assert.Equal(maintenance[0], Assert.Single(env.CurrentRows(b)).AssignedEmployeeId);
        Assert.Contains(env.Assignments.Added, x => x.TicketId == a.TicketId && x.AssignedEmployeeId == facilities[0] && !x.IsCurrent);
        Assert.Equal(facilities[0], env.Rotation.Rows[env.Destination.DepartmentId].LastAssignedEmployeeId);   // Facilities' cursor did not move again
    }

    [Fact]
    public async Task ConsecutiveTransfers_SkipAnEmployeeWhoBecomesUnavailableBetweenTransfers()
    {
        var env = new Env();
        var staff = env.AddStaff(env.Destination, 3);
        var (first, _) = await env.OwnedTicketAsync(env.Source, 1);
        await env.TransferAsync(first, env.Destination);
        Assert.Equal(staff[0], first.CurrentOwnerEmployeeId);

        env.Accounts.LockOut(staff[1]);                   // the next one in order goes off duty
        var (second, _) = await env.OwnedTicketAsync(env.Source, 2);
        await env.TransferAsync(second, env.Destination);
        Assert.Equal(staff[2], second.CurrentOwnerEmployeeId);
    }

    // ---- 5. concurrency ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentTransfers_IntoTheSameDepartment_AreSerializedByTheCursorLock_NeverDoubleDealing()
    {
        var env = new Env();
        var staff = env.AddStaff(env.Destination, 2);
        var (first, _) = await env.OwnedTicketAsync(env.Source, 1);
        var (second, _) = await env.OwnedTicketAsync(env.Source, 2);

        // Deterministic interleaving: transfer A takes the cursor lock and is parked there; transfer B starts while A holds it.
        var aInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parked = 0;
        env.Rotation.WhileLocked = async _ =>
        {
            if (Interlocked.Increment(ref parked) == 1)
            {
                aInside.SetResult();
                await release.Task;
            }
        };

        var transferA = env.TransferAsync(first, env.Destination);
        await aInside.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var transferB = env.TransferAsync(second, env.Destination);

        await Task.Delay(150);
        Assert.False(transferB.IsCompleted);                                  // B is blocked on the lock, not racing A for the same slot
        Assert.Equal(1, Volatile.Read(ref parked));

        release.SetResult();
        var results = await Task.WhenAll(transferA, transferB).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, r => Assert.Equal(TicketMutationOutcome.Success, r.Outcome));
        Assert.Equal(staff[0], first.CurrentOwnerEmployeeId);                 // A got the first slot,
        Assert.Equal(staff[1], second.CurrentOwnerEmployeeId);                // B saw the ADVANCED cursor and got the next one
        Assert.Equal(staff[1], env.Rotation.Rows[env.Destination.DepartmentId].LastAssignedEmployeeId);
        Assert.Equal(staff[0], Assert.Single(env.CurrentRows(first)).AssignedEmployeeId);
        Assert.Equal(staff[1], Assert.Single(env.CurrentRows(second)).AssignedEmployeeId);
    }

    [Fact]
    public async Task ManyConcurrentTransfers_IntoOneDepartment_DealEachEmployeeEvenly_WithExactlyOneCurrentAssignmentPerTicket()
    {
        var env = new Env();
        var staff = env.AddStaff(env.Destination, 3);
        var tickets = new List<Ticket>();
        for (var i = 1; i <= 9; i++)
        {
            tickets.Add((await env.OwnedTicketAsync(env.Source, i)).Ticket);
        }

        // Real interleaving: every transfer yields while it holds the cursor lock, so the others genuinely queue behind it.
        env.Rotation.WhileLocked = _ => Task.Delay(5);
        var results = await Task.WhenAll(tickets.Select(t => Task.Run(() => env.TransferAsync(t, env.Destination)))).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.All(results, r => Assert.Equal(TicketMutationOutcome.Success, r.Outcome));
        Assert.All(tickets, t => Assert.Single(env.CurrentRows(t)));
        var counts = tickets.GroupBy(t => t.CurrentOwnerEmployeeId!.Value).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(3, counts.Count);
        Assert.All(staff, s => Assert.Equal(3, counts[s]));                   // nobody dealt twice in a row while another waited: 9 tickets / 3 staff
        Assert.Equal(9, env.Assignments.Added.Count(a => a.IsCurrent));
    }

    [Fact]
    public async Task ATransferThatLosesTheRowVersionRace_RollsBack_AndDoesNotConsumeARoundRobinSlot()
    {
        var env = new Env();
        var staff = env.AddStaff(env.Destination, 2);
        var (loser, _) = await env.OwnedTicketAsync(env.Source, 1);
        var (next, _) = await env.OwnedTicketAsync(env.Source, 2);

        env.InnerUnitOfWork.ThrowTicketConcurrencyConflictOnCall = 1;         // another request changed the ticket first
        var lost = await env.TransferAsync(loser, env.Destination);

        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, lost.Outcome);
        Assert.Equal(1, env.InnerUnitOfWork.TransactionsRolledBack);
        Assert.Equal(0, env.InnerUnitOfWork.TransactionsCommitted);
        Assert.Null(env.Rotation.Rows[env.Destination.DepartmentId].LastAssignedEmployeeId);   // the cursor rolled back with the transaction
        Assert.Empty(env.Outbox.Committed);                                                    // no notification for an assignment that never committed

        // The slot the loser never really took goes to the next transfer, so the order is not corrupted.
        await env.TransferAsync(next, env.Destination);
        Assert.Equal(staff[0], next.CurrentOwnerEmployeeId);
    }

    // ---- 6. permissions, department access and validation are unchanged ------------------------------------------------------------

    [Fact]
    public async Task Transfer_ByAnUnauthorizedRole_IsForbidden_BeforeAnyAssignmentHappens()
    {
        var env = new Env();
        env.AddStaff(env.Destination, 2);
        var (ticket, previous) = await env.OwnedTicketAsync(env.Source, 1);

        var result = await env.TransferAsync(ticket, env.Destination, roles: [Roles.DepartmentEmployee]);

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
        Assert.Equal(env.Source.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(previous, ticket.CurrentOwnerEmployeeId);
        Assert.Empty(env.Rotation.Rows);
        Assert.Empty(env.Audit.Entries);
        Assert.Equal(0, env.InnerUnitOfWork.TransactionsBegun);
    }

    [Fact]
    public async Task Transfer_ToAnInactiveOrTheSameDepartment_IsRefused_AndAssignsNobody()
    {
        var env = new Env();
        env.AddStaff(env.Destination, 2);
        var inactive = env.Departments.AddDepartment("Archive", "ARC", isActive: false);
        env.AddStaff(inactive, 1);
        var (ticket, previous) = await env.OwnedTicketAsync(env.Source, 1);

        Assert.Equal(TicketMutationOutcome.TargetDepartmentInactive, (await env.TransferAsync(ticket, inactive)).Outcome);
        Assert.Equal(TicketMutationOutcome.AlreadyInTargetDepartment, (await env.TransferAsync(ticket, env.Source)).Outcome);

        Assert.Equal(env.Source.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(previous, ticket.CurrentOwnerEmployeeId);
        Assert.Empty(env.Rotation.Rows);
    }

    [Fact]
    public async Task Transfer_OfAClosedTicket_IsRefused_AndAssignsNobody()
    {
        var env = new Env();
        env.AddStaff(env.Destination, 2);
        var (ticket, _) = await env.OwnedTicketAsync(env.Source, 1);
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        ticket.Close();

        var result = await env.TransferAsync(ticket, env.Destination);

        Assert.Equal(TicketMutationOutcome.TicketClosed, result.Outcome);
        Assert.Empty(env.Rotation.Rows);
    }

    [Fact]
    public async Task Transfer_OfAResolvedTicket_KeepsItsOwner_BecauseAutomaticAssignmentDoesNotApplyToResolvedWork()
    {
        var env = new Env();
        env.AddStaff(env.Destination, 2);
        var (ticket, previous) = await env.OwnedTicketAsync(env.Source, 1);
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);

        var result = await env.TransferAsync(ticket, env.Destination);

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(env.Destination.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(previous, ticket.CurrentOwnerEmployeeId);
        Assert.Empty(env.Rotation.Rows);
    }
}
