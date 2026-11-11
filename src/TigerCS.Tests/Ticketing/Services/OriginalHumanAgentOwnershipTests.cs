using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// Original Human Agent ownership and return routing. A Human Agent who creates a ticket by hand is its Original Human Agent from the first
/// moment; a ticket created by Genesys AI / an integration starts with none and the first eligible agent to be dealt, accept or answer it becomes
/// it. It is write-once. A ticket transferred BACK to the Call Center returns to that agent (not the rotation) while they are eligible.
/// Scenario: Kawthar creates -> Collections -> back to Call Center -> Kawthar, never Bader.
/// </summary>
public class OriginalHumanAgentOwnershipTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);

    private sealed class FakeRotation : IAssignmentRotationRepository
    {
        public Dictionary<int, DepartmentAssignmentRotation> Rows { get; } = [];

        public Task<DepartmentAssignmentRotation> GetForUpdateAsync(int departmentId, DateTime nowUtc, CancellationToken cancellationToken = default)
        {
            if (!Rows.TryGetValue(departmentId, out var row))
            {
                Rows[departmentId] = row = new DepartmentAssignmentRotation(departmentId, nowUtc);
            }

            return Task.FromResult(row);
        }
    }

    private sealed class Env
    {
        public FakeIntakeRecordRepository Intakes { get; } = new();
        public FakeCategoryRepository Categories { get; } = new();
        public FakeDepartmentRepository Departments { get; } = new();
        public FakeTicketRepository Tickets { get; } = new();
        public FakeAuditEntryWriter Audit { get; } = new();
        public FakeUserDepartmentAssignmentRepository Members { get; } = new();
        public FakeTicketAssignmentRepository Assignments { get; } = new();
        public FakeRequestTypeAssignmentRuleRepository Rules { get; } = new();
        public FakeDepartmentWorkflowSettingsRepository Settings { get; } = new();
        public FakeRequestTypeRepository RequestTypes { get; } = new();
        public FakeTicketWorkflowEventRepository Events { get; } = new();
        public FakeRotation Rotation { get; } = new();
        public FakeTicketingUnitOfWork UnitOfWork { get; } = new();
        public TicketAutoAssignmentService AutoAssignment { get; }
        public TicketCreationAppService Creation { get; }
        public TicketAssignmentAppService Assignment { get; }
        public Department Cs { get; }
        public Department Collections { get; }
        public Department CallCenter { get; }

        public Env()
        {
            Cs = Departments.AddDepartment("Customer Service", "CS");
            Collections = Departments.AddDepartment("Collections", "COL");
            CallCenter = Departments.AddDepartment("Call Center", "CC");

            AutoAssignment = new TicketAutoAssignmentService(Rules, Settings, Members, Assignments, Audit, Rotation, Departments);

            var outbox = new FakeOutboxWriter();
            UnitOfWork.OutboxWriter = outbox;
            var sla = new SlaServiceFixture(Tickets, audit: Audit, unitOfWork: UnitOfWork);
            Creation = new TicketCreationAppService(
                Intakes, new FakeUnitReferenceRepository(), new FakeContactReferenceRepository(), Categories, new FakePriorityRepository(),
                Departments, Tickets, new FakeTicketRequesterSnapshotRepository(), new FakeTicketStatusHistoryRepository(), UnitOfWork, Audit,
                outbox, sla.DueDates, TimeProvider.System, RequestTypes, new FakeTicketInteractionRepository(), AutoAssignment,
                new FakeWorkflowTemplateRepository(), serviceIdentities: null, workflowEventRepository: Events);

            Assignment = new TicketAssignmentAppService(
                Tickets, Assignments, Members, Departments, UnitOfWork, Audit, TimeProvider.System, Settings, AutoAssignment,
                Events, requestTypeRepository: RequestTypes);
        }

        /// <summary>Adds members in rotation (id) order, which is what Round Robin cycles through: element 0 is dealt first.</summary>
        public Guid[] AddCallCenterAgents(int count)
        {
            var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).OrderBy(id => id).ToArray();
            foreach (var id in ids)
            {
                Members.Assignments.Add(new UserDepartmentAssignment(id, CallCenter.DepartmentId, isPrimary: true, Now, assignedByEmployeeId: null));
            }

            return ids;
        }

        public Guid AddMember(Department department)
        {
            var id = Guid.NewGuid();
            Members.Assignments.Add(new UserDepartmentAssignment(id, department.DepartmentId, isPrimary: true, Now, assignedByEmployeeId: null));
            return id;
        }

        public async Task<Ticket> CreateAsync(Guid creator, GenesysInteractionContextDto? genesys = null, string[]? roles = null)
        {
            var category = Categories.Seed(Cs.DepartmentId, $"Cat {Guid.NewGuid():N}");
            var intake = new IntakeRecord(WellKnownChannels.Phone, "+971500000001", null, false, null, priorityHint: null, creator, DateTime.UtcNow);
            await Intakes.AddAsync(intake);
            var result = await Creation.CreateAsync(
                creator,
                new CreateTicketRequestDto(intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.High, "AC not cooling", GenesysContext: genesys),
                callerRoles: roles);
            Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
            return (await Tickets.GetByIdAsync(result.Response!.TicketId))!;
        }

        public Task<TicketMutationResult> TransferAsync(Ticket ticket, Department target, Guid? caller = null) =>
            Assignment.TransferAsync(caller ?? Guid.NewGuid(), [Roles.CsManager], ticket.TicketId, new TransferTicketRequestDto(target.DepartmentId, "handoff", []));

        /// <summary>Simulates the destination department's own assignment (a Collections collector taking the ticket).</summary>
        public async Task TakenByAsync(Ticket ticket, Guid employee)
        {
            (await Assignments.GetCurrentAsync(ticket.TicketId))?.MarkSuperseded();
            ticket.AssignTo(employee);
            await Assignments.AddAsync(new TicketAssignment(ticket.TicketId, employee, ticket.CurrentDepartmentId, Now, assigningActorEmployeeId: null));
        }
    }

    // ---- 1. manual creation ----

    [Fact]
    public async Task ManualCreation_ByAHumanAgent_SetsOriginalHumanAgent_AndAssignsToThem_NotTheNextAgentInRotation()
    {
        var env = new Env();
        var agents = env.AddCallCenterAgents(2);
        var (bader, kawthar) = (agents[0], agents[1]); // Bader is first in the rotation: the old behaviour would have given him the ticket

        var ticket = await env.CreateAsync(kawthar, roles: [Roles.CsAgent]);

        Assert.Equal(kawthar, ticket.OriginalHumanAgentId);
        Assert.Equal(kawthar, ticket.CurrentOwnerEmployeeId);
        Assert.NotEqual(bader, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(env.CallCenter.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Single(env.Assignments.Added, a => a.TicketId == ticket.TicketId && a.IsCurrent && a.AssignedEmployeeId == kawthar);
        Assert.Contains(env.Audit.Entries, e => e.Action == "SelfAssign" && e.AfterValue!.Contains($"OriginalHumanAgentId={kawthar}"));
    }

    [Fact]
    public async Task ManualCreation_ByAPersonWhoCannotTakeCallCenterWork_StillRecordsThemAsOriginal_AndTheRotationOwnsIt()
    {
        var env = new Env();
        var agent = env.AddCallCenterAgents(1)[0];
        var csEmployee = env.AddMember(env.Cs); // not a Call Center member: not eligible to own a Call Center ticket

        var ticket = await env.CreateAsync(csEmployee, roles: [Roles.CsAgent]);

        Assert.Equal(csEmployee, ticket.OriginalHumanAgentId);
        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);
    }

    // ---- 2. Genesys AI / integration creation ----

    [Fact]
    public async Task AiHandledGenesysTicket_HasNoOriginalHumanAgent_AndNoOwner()
    {
        var env = new Env();
        env.AddCallCenterAgents(2);

        var ticket = await env.CreateAsync(Guid.NewGuid(), new GenesysInteractionContextDto("conv-ai"), roles: null);

        Assert.Null(ticket.OriginalHumanAgentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task IntegrationCreatedTicket_NeedingAPerson_GetsItsOriginalAgentFromTheFirstEligibleAgentAssigned()
    {
        var env = new Env();
        var agents = env.AddCallCenterAgents(2);
        var integrationAccount = Guid.NewGuid();

        var ticket = await env.CreateAsync(integrationAccount, new GenesysInteractionContextDto("conv-1", RequiresHumanFollowUp: true), roles: null);

        Assert.NotEqual(integrationAccount, ticket.OriginalHumanAgentId); // the integration account is never the Original Human Agent
        Assert.Equal(agents[0], ticket.CurrentOwnerEmployeeId);
        Assert.Equal(agents[0], ticket.OriginalHumanAgentId);
    }

    [Fact]
    public async Task AiTicket_FirstEligibleAgentToAccept_BecomesOriginal_AndALaterAgentNeverReplacesThem()
    {
        var env = new Env();
        var agents = env.AddCallCenterAgents(2);
        var ticket = await env.CreateAsync(Guid.NewGuid(), new GenesysInteractionContextDto("conv-ai"), roles: null);
        Assert.Null(ticket.OriginalHumanAgentId);

        var first = await env.AutoAssignment.AssignConfirmedHandlerAsync(ticket, agents[0], AutoAssignmentTrigger.CallCenterIntake, Now, Guid.NewGuid());
        Assert.Equal(agents[0], first!.AssignedEmployeeId);
        Assert.Equal(agents[0], ticket.OriginalHumanAgentId);

        // Manual reassignment to the other agent (a member of the ticket's department) moves the owner, not the original.
        env.Members.Assignments.Add(new UserDepartmentAssignment(agents[1], env.Cs.DepartmentId, isPrimary: false, Now, assignedByEmployeeId: null));
        var reassigned = await env.Assignment.AssignAsync(Guid.NewGuid(), [Roles.CsManager], ticket.TicketId, new AssignTicketRequestDto(agents[1], [], TicketTransferReason.NeedsEscalation, "balance"));
        Assert.Equal(TicketMutationOutcome.Success, reassigned.Outcome);
        Assert.Equal(agents[1], ticket.CurrentOwnerEmployeeId);
        Assert.Equal(agents[0], ticket.OriginalHumanAgentId);
    }

    [Fact]
    public async Task AnIneligibleAgent_IsNeverRecordedAsOriginal()
    {
        var env = new Env();
        env.AddCallCenterAgents(1);
        var outsider = env.AddMember(env.Cs);
        var ticket = await env.CreateAsync(Guid.NewGuid(), new GenesysInteractionContextDto("conv-ai"), roles: null);

        var recorded = await env.AutoAssignment.RecordOriginalHumanAgentAsync(ticket, outsider, "test", Guid.NewGuid());

        Assert.False(recorded);
        Assert.Null(ticket.OriginalHumanAgentId);
    }

    // ---- 3-5. preservation, transfers, return to Call Center ----

    [Fact]
    public async Task Scenario_KawtharCreates_Collections_BackToCallCenter_ReturnsToKawthar_NotBader()
    {
        var env = new Env();
        var agents = env.AddCallCenterAgents(2);
        var (bader, kawthar) = (agents[0], agents[1]);
        var collector = env.AddMember(env.Collections);

        var ticket = await env.CreateAsync(kawthar, roles: [Roles.CsAgent]);
        Assert.Equal(kawthar, ticket.OriginalHumanAgentId);

        var statusBefore = ticket.TicketStatus;

        // Kawthar transfers it to Collections; the collector takes it there.
        Assert.Equal(TicketMutationOutcome.Success, (await env.TransferAsync(ticket, env.Collections, kawthar)).Outcome);
        await env.TakenByAsync(ticket, collector);
        Assert.Equal(env.Collections.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(kawthar, ticket.OriginalHumanAgentId);                   // a department transfer never overwrites it

        // Collections processes it and sends it back.
        var back = await env.TransferAsync(ticket, env.CallCenter, collector);

        Assert.Equal(TicketMutationOutcome.Success, back.Outcome);
        Assert.Equal(env.CallCenter.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(kawthar, ticket.CurrentOwnerEmployeeId);
        Assert.NotEqual(bader, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(kawthar, ticket.OriginalHumanAgentId);
        Assert.Single(env.Assignments.Added, a => a.TicketId == ticket.TicketId && a.IsCurrent);   // exactly one current assignment
        Assert.Contains(env.Audit.Entries, e => e.Action == "AutoAssign" && e.AfterValue!.Contains("Mode=ReturnToOriginalHumanAgent"));
        Assert.Equal(statusBefore, ticket.TicketStatus);                                  // status untouched by routing
    }

    [Fact]
    public async Task ReturnToCallCenter_WhenTheOriginalAgentStillOwnsTheTicket_WritesNothingNew()
    {
        var env = new Env();
        var agents = env.AddCallCenterAgents(2);
        var kawthar = agents[1];
        var ticket = await env.CreateAsync(kawthar, roles: [Roles.CsAgent]);

        await env.TransferAsync(ticket, env.Collections, kawthar);   // the agent is preserved through the transfer
        var rowsBefore = env.Assignments.Added.Count;
        await env.TransferAsync(ticket, env.CallCenter, kawthar);

        Assert.Equal(kawthar, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(rowsBefore, env.Assignments.Added.Count);
        Assert.Equal(kawthar, ticket.OriginalHumanAgentId);
    }

    [Fact]
    public async Task ReturnToCallCenter_WhenTheOriginalAgentIsNoLongerACallCenterMember_GoesToTheQueue_WithTheReasonRecorded()
    {
        var env = new Env();
        var agents = env.AddCallCenterAgents(2);
        var (bader, kawthar) = (agents[0], agents[1]);
        var collector = env.AddMember(env.Collections);
        var ticket = await env.CreateAsync(kawthar, roles: [Roles.CsAgent]);
        await env.TransferAsync(ticket, env.Collections, kawthar);
        await env.TakenByAsync(ticket, collector);

        env.Members.Assignments.RemoveAll(a => a.EmployeeId == kawthar);   // Kawthar left the Call Center

        await env.TransferAsync(ticket, env.CallCenter, collector);

        Assert.NotEqual(kawthar, ticket.CurrentOwnerEmployeeId);
        Assert.NotEqual(collector, ticket.CurrentOwnerEmployeeId);          // the collector does not keep a Call Center ticket
        Assert.Equal(bader, ticket.CurrentOwnerEmployeeId);                 // the ordinary Call Center assignment applies
        Assert.Equal(kawthar, ticket.OriginalHumanAgentId);                 // still recorded
        Assert.Contains(env.Audit.Entries, e =>
            e.Action == "ReturnToCallCenterQueue" && e.AfterValue!.Contains("Reason=OriginalAgentIneligible") && e.AfterValue.Contains($"OriginalHumanAgentId={kawthar}"));
    }

    [Fact]
    public async Task ReturnToCallCenter_ForATicketWithNoOriginalAgent_UsesTheOrdinaryCallCenterAssignment()
    {
        var env = new Env();
        var agents = env.AddCallCenterAgents(2);
        var ticket = await env.CreateAsync(Guid.NewGuid(), new GenesysInteractionContextDto("conv-ai"), roles: null);
        Assert.Null(ticket.OriginalHumanAgentId);
        await env.TransferAsync(ticket, env.Collections);

        await env.TransferAsync(ticket, env.CallCenter);

        Assert.Equal(agents[0], ticket.CurrentOwnerEmployeeId);
        Assert.Equal(agents[0], ticket.OriginalHumanAgentId);   // first eligible agent assigned
    }

    // ---- repeated calls / background jobs ----

    [Fact]
    public async Task RepeatedAssignmentAttempts_NeverChangeAnExistingAssigneeOrTheOriginal()
    {
        var env = new Env();
        var agents = env.AddCallCenterAgents(2);
        var kawthar = agents[1];
        var ticket = await env.CreateAsync(kawthar, roles: [Roles.CsAgent]);
        var rows = env.Assignments.Added.Count;

        for (var i = 0; i < 3; i++)
        {
            var again = await env.AutoAssignment.EnsureCsAgentAsync(ticket, null, AutoAssignmentTrigger.ManualRetry, Now, Guid.NewGuid());
            Assert.Equal(AutoAssignmentOutcome.AlreadyAssigned, again.Outcome);
            Assert.False(await env.AutoAssignment.RouteExistingTicketToCallCenterAsync(ticket, Now, Guid.NewGuid(), "repeat call"));
            Assert.False(await env.AutoAssignment.RecordOriginalHumanAgentAsync(ticket, agents[0], "repeat", Guid.NewGuid()));
        }

        Assert.Equal(kawthar, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(kawthar, ticket.OriginalHumanAgentId);
        Assert.Equal(rows, env.Assignments.Added.Count);
    }

    [Fact]
    public async Task BackgroundAssignment_OfAnUnownedCallCenterTicket_PrefersTheOriginalAgent_OverTheRotation()
    {
        var env = new Env();
        var agents = env.AddCallCenterAgents(2);
        var (bader, kawthar) = (agents[0], agents[1]);
        var ticket = await env.CreateAsync(kawthar, roles: [Roles.CsAgent]);
        (await env.Assignments.GetCurrentAsync(ticket.TicketId))!.MarkSuperseded();
        ticket.ReleaseOwner();

        var result = await env.AutoAssignment.EnsureCsAgentAsync(ticket, null, AutoAssignmentTrigger.ManualRetry, Now, Guid.NewGuid());

        Assert.Equal(kawthar, result.AssignedEmployeeId);
        Assert.NotEqual(bader, ticket.CurrentOwnerEmployeeId);
    }

    // ---- 6. manual reassignment ----

    [Fact]
    public async Task ManualReassignment_ChangesTheOwner_ButNeverTheOriginalHumanAgent()
    {
        var env = new Env();
        var agents = env.AddCallCenterAgents(2);
        var (bader, kawthar) = (agents[0], agents[1]);
        var ticket = await env.CreateAsync(kawthar, roles: [Roles.CsAgent]);

        var result = await env.Assignment.AssignAsync(
            Guid.NewGuid(), [Roles.CsManager], ticket.TicketId,
            new AssignTicketRequestDto(bader, [], TicketTransferReason.NeedsEscalation, "workload"));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(bader, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(kawthar, ticket.OriginalHumanAgentId);

        // Returning from a department afterwards still goes to the Original Human Agent, not the reassigned owner.
        await env.TransferAsync(ticket, env.Collections, bader);
        await env.TransferAsync(ticket, env.CallCenter, bader);
        Assert.Equal(kawthar, ticket.CurrentOwnerEmployeeId);
        Assert.Equal(kawthar, ticket.OriginalHumanAgentId);
    }

    [Fact]
    public void OriginalHumanAgent_IsWriteOnce()
    {
        var ticket = Ticket.CreateUnverified("TG-CS-20261111-0001", 1, 5, (byte)PriorityLevel.High, "AC", DateTime.UtcNow, null, null);
        var first = Guid.NewGuid();

        Assert.True(ticket.RecordOriginalHumanAgent(first));
        Assert.False(ticket.RecordOriginalHumanAgent(Guid.NewGuid()));
        ticket.AssignTo(Guid.NewGuid());
        ticket.TransferToDepartment(2);

        Assert.Equal(first, ticket.OriginalHumanAgentId);
        Assert.Throws<ArgumentException>(() => ticket.RecordOriginalHumanAgent(Guid.Empty));
    }
}
