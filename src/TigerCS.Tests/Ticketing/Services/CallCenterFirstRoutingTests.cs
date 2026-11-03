using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// FINAL routing rule: the Call Center is the FIRST human-handling department for every channel. A new ticket that needs a person enters it
/// and is given to ONE eligible Call Center agent by Round Robin; that agent resolves/closes it or transfers it, and the transfer assigns
/// ONE eligible employee of the destination department by Round Robin (unless an explicit rule decides). AI-handled tickets are not routed.
/// </summary>
public class CallCenterFirstRoutingTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);

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
        public Department? CallCenter { get; private set; }

        public Env(bool withCallCenter = true)
        {
            Cs = Departments.AddDepartment("Customer Service", "CS");
            if (withCallCenter)
            {
                CallCenter = Departments.AddDepartment("Call Center", "CC");
            }

            AutoAssignment = new TicketAutoAssignmentService(
                Rules, Settings, Members, Assignments, Audit, Rotation, Departments);

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

        public Guid AddMember(Department department)
        {
            var id = Guid.NewGuid();
            Members.Assignments.Add(new UserDepartmentAssignment(id, department.DepartmentId, isPrimary: true, Now, assignedByEmployeeId: null));
            return id;
        }

        /// <summary>Adds members and returns them in the rotation's own (id) order, which is what Round Robin cycles through.</summary>
        public Guid[] AddMembers(Department department, int count) =>
            Enumerable.Range(0, count).Select(_ => AddMember(department)).OrderBy(id => id).ToArray();

        public async Task<TicketCreationResult> CreateManualAsync(
            Department categoryDepartment, GenesysInteractionContextDto? genesys = null, bool? queue = null, string[]? roles = null,
            byte channelId = WellKnownChannels.Phone)
        {
            var category = Categories.Seed(categoryDepartment.DepartmentId, $"Cat {Guid.NewGuid():N}");
            var creator = Guid.NewGuid();
            var intake = new IntakeRecord(
                channelId, "+971500000001", null, false, null, priorityHint: null, creator, DateTime.UtcNow);
            await Intakes.AddAsync(intake);
            return await Creation.CreateAsync(
                creator,
                new CreateTicketRequestDto(
                    intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.High, "AC not cooling",
                    GenesysContext: genesys, AssignToDepartmentQueue: queue),
                callerRoles: roles ?? [Roles.CsAgent]);
        }
    }

    private static Ticket Stored(Env env, TicketCreationResult result) =>
        env.Tickets.GetByIdAsync(result.Response!.TicketId).GetAwaiter().GetResult()!;

    // ---- 1-2: every channel enters the Call Center, one agent by Round Robin ----

    [Fact]
    public async Task ManualTicket_EntersCallCenter_AssignedToOneCallCenterAgentByRoundRobin()
    {
        var env = new Env();
        var agents = env.AddMembers(env.CallCenter!, 2);
        var (agentA, agentB) = (agents[0], agents[1]);
        env.AddMember(env.Cs); // an eligible CS employee must NOT receive a Call Center ticket

        var first = await env.CreateManualAsync(env.Cs);
        var second = await env.CreateManualAsync(env.Cs);
        var third = await env.CreateManualAsync(env.Cs);

        Assert.Equal(TicketCreationOutcome.Success, first.Outcome);
        var t1 = Stored(env, first);
        Assert.Equal(env.CallCenter!.DepartmentId, t1.CurrentDepartmentId);
        Assert.Equal(env.Cs.DepartmentId, t1.OriginatingDepartmentId); // origin preserved
        Assert.Equal(agentA, t1.CurrentOwnerEmployeeId);
        Assert.Equal(agentB, Stored(env, second).CurrentOwnerEmployeeId);
        Assert.Equal(agentA, Stored(env, third).CurrentOwnerEmployeeId);

        // Single ownership: exactly one current assignment per ticket, in the Call Center.
        Assert.Equal(3, env.Assignments.Added.Count(a => a.IsCurrent));
        Assert.All(env.Assignments.Added, a => Assert.Equal(env.CallCenter.DepartmentId, a.AssignedDepartmentId));
        Assert.Contains(env.Audit.Entries, w => w.Action == "Transfer" && w.AfterValue!.Contains("Call Center first"));
        Assert.Contains(env.Audit.Entries, w => w.Action == "AutoAssign" && w.AfterValue!.Contains("Trigger=CallCenterIntake"));
    }

    /// <summary>Regression (TG-LCS-…-0015): whatever the channel and whatever department the category belongs to, a human-handled ticket enters Call Center and gets exactly one agent.</summary>
    [Theory]
    [InlineData(WellKnownChannels.Phone)]
    [InlineData(WellKnownChannels.AppOrWebsite)]
    [InlineData(WellKnownChannels.WhatsAppOrLiveChat)]
    [InlineData(WellKnownChannels.SocialMediaDirectMessage)]
    [InlineData(WellKnownChannels.FaceToFaceKiosk)]
    [InlineData(WellKnownChannels.WhatsApp)]
    [InlineData(WellKnownChannels.LiveChat)]
    [InlineData(WellKnownChannels.Website)]
    [InlineData(WellKnownChannels.MobileApp)]
    [InlineData(WellKnownChannels.Instagram)]
    [InlineData(WellKnownChannels.Facebook)]
    public async Task EveryManualChannel_FromAnyOriginatingDepartment_EntersCallCenterWithOneAgent(byte channelId)
    {
        var env = new Env();
        var leasing = env.Departments.AddDepartment("Leasing Customer Services", "LCS");
        var accounting = env.Departments.AddDepartment("Accounting", "ACC");
        var agent = env.AddMember(env.CallCenter!);
        env.AddMember(leasing);
        env.AddMember(accounting);

        foreach (var origin in new[] { leasing, accounting, env.Cs })
        {
            var result = await env.CreateManualAsync(origin, channelId: channelId, roles: [Roles.CsAgent]);

            Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
            var ticket = Stored(env, result);
            Assert.Equal(env.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
            Assert.Equal(origin.DepartmentId, ticket.OriginatingDepartmentId);
            Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);
            Assert.Single(env.Assignments.Added, a => a.TicketId == ticket.TicketId && a.IsCurrent);
        }
    }

    [Fact]
    public async Task CreatorWhoIsACsAgent_IsNotSelfAssigned_TheRotationDecides()
    {
        var env = new Env();
        var ccAgent = env.AddMember(env.CallCenter!);

        var result = await env.CreateManualAsync(env.Cs, roles: [Roles.CsAgent]);

        Assert.Equal(ccAgent, Stored(env, result).CurrentOwnerEmployeeId);
        Assert.DoesNotContain(env.Audit.Entries, w => w.Action == "SelfAssign");
    }

    [Fact]
    public async Task GenesysTicketNeedingAPerson_EntersCallCenter()
    {
        var env = new Env();
        var agent = env.AddMember(env.CallCenter!);

        var result = await env.CreateManualAsync(env.Cs, new GenesysInteractionContextDto("conv-1", RequiresHumanFollowUp: true));

        var ticket = Stored(env, result);
        Assert.Equal(env.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task AiHandledGenesysTicket_IsNotRoutedToCallCenter()
    {
        var env = new Env();
        env.AddMember(env.CallCenter!);

        var result = await env.CreateManualAsync(env.Cs, new GenesysInteractionContextDto("conv-ai"));

        var ticket = Stored(env, result);
        Assert.Equal(env.Cs.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.DoesNotContain(env.Audit.Entries, w => w.Action == "Transfer");
    }

    [Fact]
    public async Task DepartmentQueueRequested_DoesNotBypassTheMandatoryCallCenterAssignment()
    {
        var env = new Env();
        var agent = env.AddMember(env.CallCenter!);

        var result = await env.CreateManualAsync(env.Cs, queue: true);

        var ticket = Stored(env, result);
        Assert.Equal(env.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task NoEligibleCallCenterAgent_LeavesTheTicketVisiblyUnassignedInTheCallCenter()
    {
        var env = new Env();

        var result = await env.CreateManualAsync(env.Cs);

        var ticket = Stored(env, result);
        Assert.Equal(env.CallCenter!.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Contains(env.Audit.Entries, w => w.Action == "AutoAssign" && w.AfterValue!.Contains("no eligible active CS Agent"));
    }

    [Fact]
    public async Task NoCallCenterDepartmentConfigured_KeepsTheCategoryDepartment()
    {
        var env = new Env(withCallCenter: false);
        env.AddMember(env.Cs);

        var result = await env.CreateManualAsync(env.Cs, roles: [Roles.CsSupervisor]);

        Assert.Equal(env.Cs.DepartmentId, Stored(env, result).CurrentDepartmentId);
    }

    // ---- 3-5: the Call Center agent transfers; destination Round Robin; previous owner loses ownership ----

    private static async Task<Ticket> OwnedByCallCenterAgentAsync(Env env, Guid agent)
    {
        var ticket = Ticket.CreateUnverified(
            "TG-CS-20261102-0001", env.Cs.DepartmentId, categoryId: 5, (byte)PriorityLevel.High, "AC", DateTime.UtcNow, null, null);
        await env.Tickets.AddAsync(ticket);
        ticket.TransferToDepartment(env.CallCenter!.DepartmentId);
        ticket.AssignTo(agent);
        await env.Assignments.AddAsync(new TicketAssignment(ticket.TicketId, agent, env.CallCenter.DepartmentId, Now, null));
        return ticket;
    }

    [Fact]
    public async Task CallCenterOwner_CanTransfer_AndTheCsAgentIsPreserved_NoDestinationRotation_NoDuplicateAssignment()
    {
        var env = new Env();
        var agent = env.AddMember(env.CallCenter!);
        env.AddMembers(env.Cs, 2);
        var ticket = await OwnedByCallCenterAgentAsync(env, agent);
        var previousAssignment = Assert.Single(env.Assignments.Added);

        var result = await env.Assignment.TransferAsync(
            agent, [Roles.CsAgent], ticket.TicketId, new TransferTicketRequestDto(env.Cs.DepartmentId, "Needs CS", []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(env.Cs.DepartmentId, ticket.CurrentDepartmentId);
        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);          // department and CS Agent are independent
        Assert.True(previousAssignment.IsCurrent);                   // the assignment is untouched...
        Assert.Single(env.Assignments.Added);                        // ...and no duplicate row was written
        Assert.DoesNotContain(env.Audit.Entries, e => e.Action == "AutoAssign");
    }

    [Fact]
    public async Task TransfersToTheSameDepartment_KeepEachTicketsOwnCsAgent()
    {
        var env = new Env();
        var agent = env.AddMember(env.CallCenter!);
        env.AddMembers(env.Cs, 2);
        var t1 = await OwnedByCallCenterAgentAsync(env, agent);
        await env.Assignment.TransferAsync(agent, [Roles.CsAgent], t1.TicketId, new TransferTicketRequestDto(env.Cs.DepartmentId, "x", []));

        var t2 = Ticket.CreateUnverified("TG-CS-20261102-0002", env.Cs.DepartmentId, 5, (byte)PriorityLevel.High, "AC", DateTime.UtcNow, null, null);
        await env.Tickets.AddAsync(t2);
        t2.TransferToDepartment(env.CallCenter!.DepartmentId);
        t2.AssignTo(agent);
        await env.Assignments.AddAsync(new TicketAssignment(t2.TicketId, agent, env.CallCenter.DepartmentId, Now, null));
        await env.Assignment.TransferAsync(agent, [Roles.CsAgent], t2.TicketId, new TransferTicketRequestDto(env.Cs.DepartmentId, "x", []));

        Assert.Equal(agent, t1.CurrentOwnerEmployeeId);
        Assert.Equal(agent, t2.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task ADepartmentQueueRule_CannotStripTheCsAgentOnTransfer()
    {
        var env = new Env();
        var agent = env.AddMember(env.CallCenter!);
        env.AddMember(env.Cs);
        var ticket = await OwnedByCallCenterAgentAsync(env, agent);
        ticket.ClassifyRequestType(10);
        env.Rules.Add(RequestTypeAssignmentRule.ForDepartmentQueue(10));

        var result = await env.Assignment.TransferAsync(
            agent, [Roles.CsAgent], ticket.TicketId, new TransferTicketRequestDto(env.Cs.DepartmentId, "x", []));

        Assert.Equal(TicketMutationOutcome.Success, result.Outcome);
        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task OnlyTheAssignedCallCenterAgent_MayTransfer_AnotherAgentAndANonCallCenterOwnerMayNot()
    {
        var env = new Env();
        var agent = env.AddMember(env.CallCenter!);
        var colleague = env.AddMember(env.CallCenter!);
        env.AddMember(env.Cs);
        var ticket = await OwnedByCallCenterAgentAsync(env, agent);

        var other = await env.Assignment.TransferAsync(
            colleague, [Roles.CsAgent], ticket.TicketId, new TransferTicketRequestDto(env.Cs.DepartmentId, "x", []));
        Assert.Equal(TicketMutationOutcome.Forbidden, other.Outcome);

        // An owner outside the Call Center has no transfer right (CS Manager only, as before).
        var csOwner = env.AddMember(env.Cs);
        var csTicket = Ticket.CreateUnverified("TG-CS-20261102-0009", env.Cs.DepartmentId, 5, (byte)PriorityLevel.High, "AC", DateTime.UtcNow, null, null);
        await env.Tickets.AddAsync(csTicket);
        csTicket.AssignTo(csOwner);
        var denied = await env.Assignment.TransferAsync(
            csOwner, [Roles.DepartmentEmployee], csTicket.TicketId, new TransferTicketRequestDto(env.CallCenter!.DepartmentId, "x", []));
        Assert.Equal(TicketMutationOutcome.Forbidden, denied.Outcome);
    }

    [Fact]
    public async Task CallCenterAgent_MustTransferAClassifiedTicketToItsRequestTypeDepartment()
    {
        var env = new Env();
        var agent = env.AddMember(env.CallCenter!);
        env.AddMember(env.Cs);
        var other = env.Departments.AddDepartment("Collections", "COL");
        env.AddMember(other);
        var requestType = env.RequestTypes.Add(new RequestType(
            env.Cs.DepartmentId, "General Inquiry", workflowId: 1, (byte)PriorityLevel.High,
            allowAgentPriorityChange: false, allowPendingCustomer: false, allowPendingInternal: false, allowReopen: false));
        var ticket = await OwnedByCallCenterAgentAsync(env, agent);
        ticket.ClassifyRequestType(requestType.RequestTypeId);

        var wrong = await env.Assignment.TransferAsync(
            agent, [Roles.CsAgent], ticket.TicketId, new TransferTicketRequestDto(other.DepartmentId, "x", []));
        Assert.Equal(TicketMutationOutcome.TransferTargetMustMatchRequestType, wrong.Outcome);
        Assert.Equal(agent, ticket.CurrentOwnerEmployeeId);

        var right = await env.Assignment.TransferAsync(
            agent, [Roles.CsAgent], ticket.TicketId, new TransferTicketRequestDto(env.Cs.DepartmentId, "x", []));
        Assert.Equal(TicketMutationOutcome.Success, right.Outcome);
    }

    // ---- 6: every assigned operational employee resolves/closes their own ticket (kept from the previous release) ----
    // Covered by RoundRobinAndOwnerPermissionTests (owner Resolve/Close for any role, read-only roles excluded).
}
