using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

public class TicketAssignmentAppServiceTests
{
    private sealed record Fixture(
        TicketAssignmentAppService Service,
        FakeTicketRepository Tickets,
        FakeTicketAssignmentRepository Assignments,
        FakeUserDepartmentAssignmentRepository DepartmentAssignments,
        FakeDepartmentRepository Departments,
        FakeAuditEntryWriter Audit,
        FakeTicketingUnitOfWork UnitOfWork,
        FakeDepartmentWorkflowSettingsRepository WorkflowSettings,
        FakeRequestTypeAssignmentRuleRepository Rules);

    private static Fixture CreateService()
    {
        var tickets = new FakeTicketRepository();
        var assignments = new FakeTicketAssignmentRepository();
        var departmentAssignments = new FakeUserDepartmentAssignmentRepository();
        var departments = new FakeDepartmentRepository();
        var audit = new FakeAuditEntryWriter();
        var unitOfWork = new FakeTicketingUnitOfWork();
        var workflowSettings = new FakeDepartmentWorkflowSettingsRepository();
        var rules = new FakeRequestTypeAssignmentRuleRepository();

        // Transfer re-runs the real automation against the NEW department, so
        // the fixture wires the real service over fakes rather than a stub.
        var autoAssignment = new TicketAutoAssignmentService(
            rules, workflowSettings, departmentAssignments, assignments, audit);

        var service = new TicketAssignmentAppService(
            tickets, assignments, departmentAssignments, departments, unitOfWork, audit, TimeProvider.System,
            workflowSettings, autoAssignment);

        return new Fixture(service, tickets, assignments, departmentAssignments, departments, audit, unitOfWork, workflowSettings, rules);
    }

    private static async Task<Ticket> SeedTicketAsync(FakeTicketRepository repo, int departmentId = 2)
    {
        var ticket = Ticket.CreateVerified(
            "TG-CS-20260821-0001", departmentId, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, priorityId: (byte)PriorityLevel.High, "AC not cooling", DateTime.UtcNow);
        await repo.AddAsync(ticket);
        return ticket;
    }

    private static async Task<Ticket> SeedClosedTicketAsync(FakeTicketRepository repo, int departmentId = 2)
    {
        var ticket = await SeedTicketAsync(repo, departmentId);
        ticket.AssignTo(Guid.NewGuid());
        ticket.ChangeStatus(TicketStatus.InProgress);
        ticket.Resolve(ResolutionOutcome.Resolved, duplicateOfTicketId: null);
        ticket.Close();
        return ticket;
    }

    // ---- Assignment authorization matrix (PR correction) — all 9 roles ----
    // CS Agent and Department Employee: no assignment capability at all
    // (not even self-claim, which this correction removes entirely).
    // CS Supervisor / Department Head: assign/reassign within their own
    // department only. CS Manager: cross-department. GM/Chairman/Reporting
    // User: no operational assignment.
    // System Administrator: authorized — not by the permission matrix, which
    // still grants it no Assign, but by the ADR-0024 central override, which
    // TicketAssignmentAppService reaches through AuthorizationGate. This row
    // is the superseded exclusion's replacement, not a widened matrix.
    public static IEnumerable<object[]> AssignRoleMatrix() =>
    [
        [Roles.CsAgent, false],
        [Roles.DepartmentEmployee, false],
        [Roles.CsSupervisor, true],
        [Roles.DepartmentHead, true],
        [Roles.CsManager, true],
        [Roles.GeneralManager, false],
        [Roles.ChairmanCeo, false],
        [Roles.SystemAdministrator, true],
        [Roles.ReportingUser, false]
    ];

    [Theory]
    [MemberData(nameof(AssignRoleMatrix))]
    public async Task AssignAsync_AllNineRoles_MatchesCorrectedAuthorizationMatrix(string role, bool expectSuccess)
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var caller = Guid.NewGuid();
        var target = Guid.NewGuid();
        // Caller and target both belong to the ticket's department — isolates
        // the role check itself from department-scoping (covered separately
        // below), except for CS Manager, whose authority doesn't depend on it.
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(caller, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(target, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));

        var result = await f.Service.AssignAsync(
            caller, [role], ticket.TicketId, new AssignTicketRequestDto(target, []));

        Assert.Equal(expectSuccess ? TicketMutationOutcome.Success : TicketMutationOutcome.Forbidden, result.Outcome);
        if (expectSuccess)
        {
            Assert.Equal(target, ticket.CurrentOwnerEmployeeId);
        }
        else
        {
            Assert.Null(ticket.CurrentOwnerEmployeeId);
            Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
        }
    }

    [Fact]
    public async Task AssignAsync_SupervisorOfDifferentDepartment_ReturnsForbidden_DepartmentScoped()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        var caller = Guid.NewGuid();
        var target = Guid.NewGuid();
        // Supervisor, but for department 999, not the ticket's department 2.
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(caller, 999, true, DateTime.UtcNow, null));
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(target, 2, true, DateTime.UtcNow, null));

        var result = await f.Service.AssignAsync(
            caller, [Roles.CsSupervisor], ticket.TicketId, new AssignTicketRequestDto(target, []));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task AssignAsync_ByDepartmentHeadOfDifferentDepartment_ReturnsForbidden_PreventsCrossDepartmentAssignment()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        var caller = Guid.NewGuid();
        var target = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(caller, 999, true, DateTime.UtcNow, null));
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(target, 2, true, DateTime.UtcNow, null));

        var result = await f.Service.AssignAsync(
            caller, [Roles.DepartmentHead], ticket.TicketId, new AssignTicketRequestDto(target, []));

        Assert.Equal(TicketMutationOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task AssignAsync_CsManagerAcrossDepartments_Succeeds_NoOwnDepartmentMembershipRequired()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        var caller = Guid.NewGuid(); // CS Manager, member of no department at all.
        var target = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(target, 2, true, DateTime.UtcNow, null));

        var result = await f.Service.AssignAsync(
            caller, [Roles.CsManager], ticket.TicketId, new AssignTicketRequestDto(target, []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task AssignAsync_AssignedEmployeeNotInTicketDepartment_ReturnsEmployeeNotInDepartment()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var caller = Guid.NewGuid();
        var target = Guid.NewGuid();
        // CS Manager (cross-department authority, isolates this check from the caller's own department scoping).
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(target, 999, true, DateTime.UtcNow, null));

        var result = await f.Service.AssignAsync(
            caller, [Roles.CsManager], ticket.TicketId, new AssignTicketRequestDto(target, []));

        Assert.Equal(TicketMutationOutcome.EmployeeNotInDepartment, result.Outcome);
    }

    [Fact]
    public async Task AssignAsync_OnClosedTicket_ReturnsTicketClosed_NoDatabaseWrites()
    {
        var f = CreateService();
        var ticket = await SeedClosedTicketAsync(f.Tickets);
        var caller = Guid.NewGuid();
        var target = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(target, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));

        var result = await f.Service.AssignAsync(
            caller, [Roles.CsManager], ticket.TicketId, new AssignTicketRequestDto(target, []));

        Assert.Equal(TicketMutationOutcome.TicketClosed, result.Outcome);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
        Assert.Empty(f.Assignments.Added);
        Assert.DoesNotContain(f.Audit.Written, w => w.Action == "Assign");
    }

    /// <summary>
    /// Requirement 2 of the ADR-0024 correction: the override grants
    /// authorization, not immunity. A System Administrator that loses a
    /// RowVersion race gets the same 409 outcome and the same rollback as
    /// the CS Supervisor in the test below — the authorization gate is
    /// passed, the concurrency check still refuses.
    /// </summary>
    [Fact]
    public async Task AssignAsync_SystemAdministrator_ConcurrentModification_StillReturnsConcurrencyConflictAndRollsBack()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var caller = Guid.NewGuid();
        var target = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(target, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));
        f.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = 1;

        var result = await f.Service.AssignAsync(
            caller, [Roles.SystemAdministrator], ticket.TicketId, new AssignTicketRequestDto(target, []));

        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, result.Outcome);
        Assert.Equal(1, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.TransactionsCommitted);
        Assert.Equal(1, f.UnitOfWork.TransactionsRolledBack);
    }

    /// <summary>
    /// The administrator is authorized for Assign but the ticket's own
    /// validation is untouched: the assignment target must still be a member
    /// of the ticket's current department (MVP-API-Contracts.md §3.5).
    /// </summary>
    [Fact]
    public async Task AssignAsync_SystemAdministrator_TargetOutsideTheTicketsDepartment_StillReturnsEmployeeNotInDepartment()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);

        var result = await f.Service.AssignAsync(
            Guid.NewGuid(), [Roles.SystemAdministrator], ticket.TicketId,
            new AssignTicketRequestDto(Guid.NewGuid(), []));

        Assert.Equal(TicketMutationOutcome.EmployeeNotInDepartment, result.Outcome);
        Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
    }

    /// <summary>Closed-ticket immutability outranks the override: 422, not 200.</summary>
    [Fact]
    public async Task AssignAsync_SystemAdministrator_OnClosedTicket_StillReturnsTicketClosed_NoDatabaseWrites()
    {
        var f = CreateService();
        var ticket = await SeedClosedTicketAsync(f.Tickets);
        var target = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(target, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));

        var result = await f.Service.AssignAsync(
            Guid.NewGuid(), [Roles.SystemAdministrator], ticket.TicketId, new AssignTicketRequestDto(target, []));

        Assert.Equal(TicketMutationOutcome.TicketClosed, result.Outcome);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
        Assert.Empty(f.Assignments.Added);
    }

    [Fact]
    public async Task AssignAsync_ConcurrentModification_ReturnsConcurrencyConflictAndRollsBack()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var caller = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(caller, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));
        f.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = 1;

        var result = await f.Service.AssignAsync(
            caller, [Roles.CsSupervisor], ticket.TicketId, new AssignTicketRequestDto(caller, []));

        Assert.Equal(TicketMutationOutcome.ConcurrencyConflict, result.Outcome);
        Assert.Equal(1, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.TransactionsCommitted);
        Assert.Equal(1, f.UnitOfWork.TransactionsRolledBack);
    }

    // ---- Transfer authorization matrix: the Call Center layer, scoped per ticket (CS Manager any ticket;
    // CS Agent/Supervisor their own ticket; Supervisor also within a department they belong to),
    // plus System Administrator via the ADR-0024 central override. Here the caller is a member of the
    // ticket's department but NOT its owner. ----
    public static IEnumerable<object[]> TransferRoleMatrix() =>
    [
        [Roles.CsAgent, false],
        [Roles.DepartmentEmployee, false],
        [Roles.CsSupervisor, true],
        [Roles.DepartmentHead, false],
        [Roles.CsManager, true],
        [Roles.GeneralManager, false],
        [Roles.ChairmanCeo, false],
        [Roles.SystemAdministrator, true],
        [Roles.ReportingUser, false]
    ];

    [Theory]
    [MemberData(nameof(TransferRoleMatrix))]
    public async Task TransferAsync_AllNineRoles_MatchesCorrectedAuthorizationMatrix(string role, bool expectSuccess)
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        var csAgent = Guid.NewGuid();
        ticket.AssignTo(csAgent);
        var targetDepartment = f.Departments.AddDepartment("Facility Management " + role, "FM-" + role[..Math.Min(3, role.Length)]);
        var caller = Guid.NewGuid();
        // Even Department Head/Supervisor are department-scoped to the
        // ticket's own department here, to prove the rejection is role-based
        // (transfer is CS-Manager-only now), not a department-scoping gap.
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(caller, 2, true, DateTime.UtcNow, null));

        var result = await f.Service.TransferAsync(
            caller, [role], ticket.TicketId, new TransferTicketRequestDto(targetDepartment.DepartmentId, "Misrouted", []));

        Assert.Equal(expectSuccess ? TicketMutationOutcome.Success : TicketMutationOutcome.Forbidden, result.Outcome);
        if (expectSuccess)
        {
            Assert.Equal(targetDepartment.DepartmentId, ticket.CurrentDepartmentId);
            Assert.Equal(csAgent, ticket.CurrentOwnerEmployeeId);   // department and CS Agent are independent
        }
        else
        {
            Assert.Equal(2, ticket.CurrentDepartmentId);
            Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
        }
    }

    [Theory]
    [InlineData(Roles.CsAgent, true, true)]          // owner: may transfer their own ticket
    [InlineData(Roles.CsSupervisor, true, true)]
    [InlineData(Roles.CsAgent, false, false)]        // not owner, even as a department member: refused
    [InlineData(Roles.CsSupervisor, false, false)]   // not owner, not a member: refused (no global reach)
    [InlineData(Roles.DepartmentHead, true, false)]  // owner but not a Call Center role
    [InlineData(Roles.DepartmentEmployee, true, false)]
    [InlineData(Roles.ChairmanCeo, true, false)]
    public async Task TransferAsync_IsScopedToTicketsTheCallCenterUserOwns(string role, bool isOwner, bool expectSuccess)
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        var caller = Guid.NewGuid();
        ticket.AssignTo(isOwner ? caller : Guid.NewGuid());
        var target = f.Departments.AddDepartment("Collections " + role, "COL-" + role[..3]);

        var result = await f.Service.TransferAsync(
            caller, [role], ticket.TicketId, new TransferTicketRequestDto(target.DepartmentId, "Wrong team", []));

        Assert.Equal(expectSuccess ? TicketMutationOutcome.Success : TicketMutationOutcome.Forbidden, result.Outcome);
        Assert.Equal(expectSuccess ? target.DepartmentId : 2, ticket.CurrentDepartmentId);
    }

    [Fact]
    public async Task TransferAsync_ByCsAgent_KeepsTheAgent_AndDoesNotRotateInTheTargetDepartment()
    {
        var f = CreateService();
        var agent = Guid.NewGuid();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        ticket.AssignTo(agent);
        var target = f.Departments.AddDepartment("Collections", "COL");
        var eligible = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(eligible, target.DepartmentId, true, DateTime.UtcNow, null));
        var roles = new FakeUserRoleReader().Add(eligible, Roles.DepartmentEmployee);
        var rotation = new Dictionary<int, DepartmentAssignmentRotation>();
        var autoAssignment = new TicketAutoAssignmentService(
            f.Rules, f.WorkflowSettings, f.DepartmentAssignments, f.Assignments, f.Audit,
            new RotationStub(rotation), f.Departments, roles);
        var service = new TicketAssignmentAppService(
            f.Tickets, f.Assignments, f.DepartmentAssignments, f.Departments, f.UnitOfWork, f.Audit, TimeProvider.System,
            f.WorkflowSettings, autoAssignment);

        var result = await service.TransferAsync(
            agent, [Roles.CsAgent], ticket.TicketId, new TransferTicketRequestDto(target.DepartmentId, "Wrong team", []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(target.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);
        Assert.Empty(rotation);   // the destination department's own rotation is not consulted
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AutoAssign");
    }

    private sealed class RotationStub(Dictionary<int, DepartmentAssignmentRotation> rows) : IAssignmentRotationRepository
    {
        public Task<DepartmentAssignmentRotation> GetForUpdateAsync(int departmentId, DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            if (!rows.TryGetValue(departmentId, out var row))
            {
                rows[departmentId] = row = new DepartmentAssignmentRotation(departmentId, nowUtc);
            }

            return Task.FromResult(row);
        }
    }

    [Fact]
    public async Task TransferAsync_ByCsManager_MovesDepartmentAndKeepsTheCsAgent()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        var csAgent = Guid.NewGuid();
        ticket.AssignTo(csAgent);
        var targetDepartment = f.Departments.AddDepartment("Facility Management", "FM");

        var result = await f.Service.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(targetDepartment.DepartmentId, "Misrouted", []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(targetDepartment.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(csAgent, ticket.CurrentOwnerEmployeeId);
        Assert.Contains(f.Audit.Entries, w => w.Action == "Transfer" && w.AfterValue!.Contains("CsAgentPreserved=true"));
        Assert.DoesNotContain(f.Audit.Entries, w => w.Action == "AutoAssign");   // no duplicate assignment event
        Assert.Empty(f.Assignments.Added);
    }

    // ---- Transfer re-runs the assignment automation for the NEW department ----
    // Department first, then Department + Request Type → rule. Every
    // non-assignable case lands in the NEW department's queue; a random
    // employee is never chosen.

    [Fact]
    public async Task TransferAsync_OfATicketWithNoCsAgent_ReEvaluatesAssignment_AndAssignsTheNewDepartmentsConfiguredEmployee()
    {
        // A ticket that already has a CS Agent keeps it (see ...KeepsTheCsAgent); only an ownerless ticket reaches the rules.
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        ticket.ClassifyRequestType(requestTypeId: 10);
        var departmentC = f.Departments.AddDepartment("Facility Management", "FM");

        // The rule's primary is a member of department C — the department the
        // ticket is moving to, not the one it came from.
        var ahmed = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(ahmed, departmentC.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        f.Rules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(10, ahmed));

        var result = await f.Service.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(departmentC.DepartmentId, "Misrouted", []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(departmentC.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(ahmed, ticket.CurrentOwnerEmployeeId);

        // The automatic assignment is recorded as a SYSTEM action against the
        // NEW department, and names the transfer as its trigger.
        var autoAssign = Assert.Single(f.Audit.Entries, e => e.Action == "AutoAssign");
        Assert.Null(autoAssign.ActorEmployeeId);
        Assert.Contains($"DepartmentId={departmentC.DepartmentId}", autoAssign.AfterValue);
        Assert.Contains("Trigger=DepartmentTransfer", autoAssign.AfterValue);

        var assignment = Assert.Single(f.Assignments.Added);
        Assert.Null(assignment.AssigningActorEmployeeId);
        Assert.Equal(departmentC.DepartmentId, assignment.AssignedDepartmentId);
    }

    [Fact]
    public async Task TransferAsync_WithAssignToEmployeeId_AssignsTheNamedMemberOfTheTargetDepartment_AndSkipsTheRules()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        ticket.ClassifyRequestType(requestTypeId: 10);
        var previousOwner = Guid.NewGuid();
        ticket.AssignTo(previousOwner);
        var departmentC = f.Departments.AddDepartment("Leasing Customer Services", "LCS");

        var sara = Guid.NewGuid();
        var ahmed = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(sara, departmentC.DepartmentId, true, DateTime.UtcNow, null));
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(ahmed, departmentC.DepartmentId, true, DateTime.UtcNow, null));
        // A rule that would pick Ahmed — the manager's explicit choice (Sara) wins.
        f.Rules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(10, ahmed));

        var manager = Guid.NewGuid();
        var result = await f.Service.TransferAsync(
            manager, [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(departmentC.DepartmentId, "Leasing question", [], AssignToEmployeeId: sara));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(departmentC.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(2, ticket.OriginatingDepartmentId);
        Assert.Equal(sara, ticket.CurrentOwnerEmployeeId);

        var assignment = Assert.Single(f.Assignments.Added);
        Assert.Equal(sara, assignment.AssignedEmployeeId);
        Assert.Equal(departmentC.DepartmentId, assignment.AssignedDepartmentId);
        Assert.Equal(manager, assignment.AssigningActorEmployeeId);

        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AutoAssign");
        var transfer = Assert.Single(f.Audit.Entries, e => e.Action == "Transfer");
        var assign = Assert.Single(f.Audit.Entries, e => e.Action == "Assign");
        Assert.Equal(transfer.CorrelationId, assign.CorrelationId);
        Assert.Contains("ViaTransfer=true", assign.AfterValue);
    }

    [Fact]
    public async Task TransferAsync_WithAssignToEmployeeId_RefusesSomeoneOutsideTheTargetDepartment_AndChangesNothing()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        var departmentC = f.Departments.AddDepartment("Leasing Customer Services", "LCS");
        var sourceOnly = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(new UserDepartmentAssignment(sourceOnly, 2, true, DateTime.UtcNow, null));

        var result = await f.Service.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(departmentC.DepartmentId, "Leasing question", [], AssignToEmployeeId: sourceOnly));

        Assert.Equal(TicketMutationOutcome.EmployeeNotInDepartment, result.Outcome);
        Assert.Equal(2, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
        Assert.Empty(f.Audit.Entries);
    }

    [Fact]
    public async Task TransferAsync_ChangesTheDepartmentBeforeReEvaluatingTheAssignment()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        ticket.ClassifyRequestType(requestTypeId: 10);
        var departmentC = f.Departments.AddDepartment("Facility Management", "FM");

        await f.Service.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(departmentC.DepartmentId, "Misrouted", []));

        // Ordering is the rule, not an accident: the Transfer entry (the
        // department change) is written before the re-evaluation entry, and
        // the re-evaluation already sees department C.
        var transferIndex = f.Audit.Entries.FindIndex(e => e.Action == "Transfer");
        var autoAssignIndex = f.Audit.Entries.FindIndex(e => e.Action == "AutoAssign");
        Assert.True(transferIndex >= 0 && autoAssignIndex > transferIndex,
            "the department transfer must be audited before the assignment re-evaluation");

        var autoAssign = f.Audit.Entries[autoAssignIndex];
        Assert.Contains($"DepartmentId={departmentC.DepartmentId}", autoAssign.AfterValue);

        // Both entries share one correlation id — one operation in history.
        var transfer = f.Audit.Entries[transferIndex];
        Assert.Equal(transfer.CorrelationId, autoAssign.CorrelationId);
    }

    [Fact]
    public async Task TransferAsync_WhenTheRuleTargetsSomeoneOutsideTheNewDepartment_FallsBackToTheNewDepartmentQueue()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        ticket.ClassifyRequestType(requestTypeId: 10);
        var departmentC = f.Departments.AddDepartment("Facility Management", "FM");

        // The configured primary belongs to the OLD department only — stale
        // for department C.
        var oldDepartmentEmployee = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(oldDepartmentEmployee, 2, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));
        f.Rules.Add(RequestTypeAssignmentRule.ForSpecificEmployee(10, oldDepartmentEmployee));

        var result = await f.Service.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(departmentC.DepartmentId, "Misrouted", []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(departmentC.DepartmentId, ticket.CurrentDepartmentId);

        // No employee is guessed: the ticket sits in department C's queue,
        // and the reason is audited.
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Empty(f.Assignments.Added);

        var autoAssign = Assert.Single(f.Audit.Entries, e => e.Action == "AutoAssign" && e.AfterValue!.Contains("Rule "));
        Assert.Contains("DepartmentQueue", autoAssign.AfterValue);
        Assert.Contains($"DepartmentId={departmentC.DepartmentId}", autoAssign.AfterValue);
        Assert.Contains("Trigger=DepartmentTransfer", autoAssign.AfterValue);
    }

    [Fact]
    public async Task TransferAsync_WithNoRuleForTheRequestType_LeavesTheTicketInTheNewDepartmentQueue()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        ticket.ClassifyRequestType(requestTypeId: 10);
        var departmentC = f.Departments.AddDepartment("Facility Management", "FM");

        var result = await f.Service.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(departmentC.DepartmentId, "Misrouted", []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(departmentC.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Empty(f.Assignments.Added);

        // The rule found nothing; the Round Robin default then had no rotation store / eligible employee, so the queue stays visibly unassigned.
        Assert.Contains(f.Audit.Entries, e => e.Action == "AutoAssign" && e.AfterValue!.Contains("No active assignment rule configured"));
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AutoAssign" && e.AfterValue!.Contains("AssignedEmployeeId="));
    }

    [Fact]
    public async Task TransferAsync_OnATicketWithoutARequestType_KeepsExactPrePhaseBehavior()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        var departmentC = f.Departments.AddDepartment("Facility Management", "FM");

        var result = await f.Service.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(departmentC.DepartmentId, "Misrouted", []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(departmentC.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);

        // No request type => no rule exists, so only the Round Robin default can assign; this fixture has no eligible employee, so the
        // queue stays visibly unassigned and nothing was assigned.
        Assert.DoesNotContain(f.Audit.Entries, e => e.Action == "AutoAssign" && e.AfterValue!.Contains("AssignedEmployeeId="));
        Assert.Empty(f.Assignments.Added);
    }

    [Fact]
    public async Task TransferAsync_ToInactiveDepartment_ReturnsTargetDepartmentInactive()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);
        var inactiveDepartment = f.Departments.AddDepartment("Legacy", "LG", isActive: false);

        var result = await f.Service.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(inactiveDepartment.DepartmentId, "Misrouted", []));

        Assert.Equal(TicketMutationOutcome.TargetDepartmentInactive, result.Outcome);
    }

    [Fact]
    public async Task TransferAsync_ToSameDepartment_ReturnsAlreadyInTargetDepartment()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets, departmentId: 2);

        var result = await f.Service.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(2, "No-op", []));

        Assert.Equal(TicketMutationOutcome.AlreadyInTargetDepartment, result.Outcome);
    }

    [Fact]
    public async Task TransferAsync_OnClosedTicket_ReturnsTicketClosed_NoDatabaseWrites()
    {
        var f = CreateService();
        var ticket = await SeedClosedTicketAsync(f.Tickets, departmentId: 2);
        var targetDepartment = f.Departments.AddDepartment("Facility Management", "FM");

        var result = await f.Service.TransferAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(targetDepartment.DepartmentId, "Misrouted", []));

        Assert.Equal(TicketMutationOutcome.TicketClosed, result.Outcome);
        Assert.Equal(2, ticket.CurrentDepartmentId);
        Assert.Equal(0, f.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, f.UnitOfWork.SaveChangesCallCount);
        Assert.DoesNotContain(f.Audit.Written, w => w.Action == "Transfer");
    }

    // ---- Workflow/Automation phase 2: department workflow settings narrow, never widen ----

    private static (Guid Caller, Guid Target) SeedAuthorizedAssignPair(Fixture f, Ticket ticket)
    {
        var caller = Guid.NewGuid();
        var target = Guid.NewGuid();
        f.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(caller, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));
        f.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(target, ticket.CurrentDepartmentId, true, DateTime.UtcNow, null));
        return (caller, target);
    }

    [Fact]
    public async Task AssignAsync_DepartmentSettingsDisableAssignment_RejectsEvenAnAuthorizedRole()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var (caller, target) = SeedAuthorizedAssignPair(f, ticket);
        f.WorkflowSettings.Add(new TigerCS.Domain.Modules.WorkflowConfiguration.DepartmentWorkflowSettings(
            ticket.CurrentDepartmentId, allowAssignment: false, allowInternalReassignment: true, allowTransferToOtherDepartments: true));

        var result = await f.Service.AssignAsync(
            caller, [Roles.CsSupervisor], ticket.TicketId, new AssignTicketRequestDto(target, []));

        Assert.Equal(TicketMutationOutcome.DisabledByDepartmentSettings, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task AssignAsync_DepartmentSettingsDisableReassignment_FirstAssignmentStillWorks_ReassignmentRejected()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var (caller, target) = SeedAuthorizedAssignPair(f, ticket);
        f.WorkflowSettings.Add(new TigerCS.Domain.Modules.WorkflowConfiguration.DepartmentWorkflowSettings(
            ticket.CurrentDepartmentId, allowAssignment: true, allowInternalReassignment: false, allowTransferToOtherDepartments: true));

        var first = await f.Service.AssignAsync(
            caller, [Roles.CsSupervisor], ticket.TicketId, new AssignTicketRequestDto(target, []));
        Assert.Equal(TicketMutationOutcome.Success, first.Outcome);

        var second = await f.Service.AssignAsync(
            caller, [Roles.CsSupervisor], ticket.TicketId, new AssignTicketRequestDto(caller, []));
        Assert.Equal(TicketMutationOutcome.DisabledByDepartmentSettings, second.Outcome);
        Assert.Equal(target, ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task AssignAsync_NoSettingsRow_BehavesExactlyAsBeforeThePhase()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var (caller, target) = SeedAuthorizedAssignPair(f, ticket);

        var result = await f.Service.AssignAsync(
            caller, [Roles.CsSupervisor], ticket.TicketId, new AssignTicketRequestDto(target, []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(target, ticket.CurrentOwnerEmployeeId);

        // Manual (re)assignment stays audited with the human actor — never
        // as a system action.
        var audit = Assert.Single(f.Audit.Written, w => w.Action == "Assign");
        Assert.Equal(caller, audit.ActorEmployeeId);
    }

    [Fact]
    public async Task TransferAsync_SourceDepartmentSettingsDisableTransferOut_Rejected()
    {
        var f = CreateService();
        var ticket = await SeedTicketAsync(f.Tickets);
        var caller = Guid.NewGuid();
        var target = f.Departments.AddDepartment("Collections", "COL");
        f.WorkflowSettings.Add(new TigerCS.Domain.Modules.WorkflowConfiguration.DepartmentWorkflowSettings(
            ticket.CurrentDepartmentId, allowAssignment: true, allowInternalReassignment: true, allowTransferToOtherDepartments: false));

        var result = await f.Service.TransferAsync(
            caller, [Roles.CsManager], ticket.TicketId,
            new TransferTicketRequestDto(target.DepartmentId, "Belongs to Collections", []));

        Assert.Equal(TicketMutationOutcome.DisabledByDepartmentSettings, result.Outcome);
        Assert.Equal(ticket.OriginatingDepartmentId, ticket.CurrentDepartmentId);
    }
}
