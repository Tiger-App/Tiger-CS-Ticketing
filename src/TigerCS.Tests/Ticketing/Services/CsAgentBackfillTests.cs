using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Application.Modules.WorkflowConfiguration.Abstractions;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.BackgroundJobs;
using TigerCS.Tests.Administration.Services;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.IdentityAndAccess.Fakes;
using TigerCS.Tests.Notifications.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>
/// The one-time CS Agent backfill: same shared rotation and cursor as new tickets, oldest first, nothing moved, AI / approvals / human-handled
/// tickets excluded, idempotent, race-safe, throttled and resumable. All over fakes; the real-database evidence query is in
/// <c>TicketUnassignedWorkRepositorySqliteTests</c>.
/// </summary>
public class CsAgentBackfillTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = Now.AddDays(-3);

    private sealed class Env
    {
        public FakeDepartmentRepository Departments { get; } = new();
        public FakeTicketRepository Tickets { get; } = new();
        public FakeAuditEntryWriter Audit { get; } = new();
        public FakeUserDepartmentAssignmentRepository Members { get; } = new();
        public FakeTicketAssignmentRepository Assignments { get; } = new();
        public FakeTicketWorkflowEventRepository Events { get; } = new();
        public FakeAssignmentRotationRepository Rotation { get; } = new();
        public FakeTicketingUnitOfWork UnitOfWork { get; } = new();
        public CallCenterUnassignedWorkReconcilerTests.StubEvidence Evidence { get; } = new();
        public FakeUserAccountManager Accounts { get; } = new();
        public Department CallCenter { get; }
        public Department Facilities { get; }
        public Department Leasing { get; }
        public CsAgentBackfillOptions Options { get; } = new() { Enabled = true, DelayBetweenTicketsMs = 0 };
        public Guid[] Agents { get; private set; } = [];

        public Env(int agents = 3)
        {
            CallCenter = Departments.AddDepartment("Call Center", "CC");
            Facilities = Departments.AddDepartment("Facilities Management", "FM");
            Leasing = Departments.AddDepartment("Leasing", "LEA");
            Agents = [.. Enumerable.Range(0, agents).Select(_ => AddAgent()).Order()];
        }

        public Guid AddAgent(string role = Roles.CsAgent)
        {
            var id = Guid.NewGuid();
            Members.Assignments.Add(new UserDepartmentAssignment(id, CallCenter.DepartmentId, true, Now, null));
            Accounts.Add(id, id.ToString("N"), role);
            return id;
        }

        public TicketAutoAssignmentService Engine() =>
            new(new FakeRequestTypeAssignmentRuleRepository(), new FakeDepartmentWorkflowSettingsRepository(), Members, Assignments, Audit, Rotation,
                Departments, roleReader: Accounts, accountManager: Accounts, workflowEventRepository: Events);

        public CsAgentBackfillService Service() =>
            new(Evidence, Tickets, Engine(), Departments, Audit, UnitOfWork, new FakeTimeProvider(Now));

        /// <summary>An unowned active ticket plus the evidence the repository would report for it.</summary>
        public async Task<Ticket> SeedAsync(
            Department department, TicketStatus status = TicketStatus.Open, DateTime? created = null,
            bool liveAi = false, bool aiEnded = false, bool pendingApproval = false, Guid? answeredBy = null, bool humanFollowUp = false,
            Guid? owner = null, DateTime? lastActivity = null, bool openHandoff = false, bool phone = false, bool humanEvidence = false)
        {
            var ticket = Ticket.CreateUnverified($"TG-FM-20261103-{Evidence.Rows.Count + 1:D4}", department.DepartmentId, 5, (byte)PriorityLevel.High, "AC", created ?? Old);
            if (status == TicketStatus.InProgress)
            {
                ticket.StartInProgressAtCreation();   // an unowned In Progress ticket, as legacy data has
            }

            await Tickets.AddAsync(ticket);
            if (owner is not null)
            {
                ticket.AssignTo(owner.Value);
            }

            Evidence.Rows[ticket.TicketId] = new UnassignedTicketEvidence(
                ticket.TicketId, ticket.TicketNumber, status, owner, department.DepartmentId, department.DepartmentId, created ?? Old,
                PendingApproval: pendingApproval, HasGenesysInteraction: liveAi || aiEnded, HasLiveGenesysInteraction: liveAi,
                RequiresHumanFollowUp: humanFollowUp, OpenHandoff: openHandoff, HumanEvidence: humanEvidence, AnsweringEmployeeId: answeredBy,
                AnsweringGenesysUserId: null, PhonePickupByUnmappedAgent: false, UnmappedGenesysAgentId: null, LastActivityUtc: lastActivity ?? created ?? Old,
                LeftCallCenter: false, ReleasedByPerson: false, LiveGenesysIsPhoneOnly: liveAi && phone);
            return ticket;
        }
    }

    // ---- classification --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Classifier_SeparatesEligibleFromEveryExclusion()
    {
        var env = new Env();
        var tickets = new Dictionary<CsAgentBackfillClass, Ticket>
        {
            [CsAgentBackfillClass.Eligible] = await env.SeedAsync(env.Facilities),
            [CsAgentBackfillClass.PendingApproval] = await env.SeedAsync(env.Facilities, pendingApproval: true),
            [CsAgentBackfillClass.ActiveAiConversation] = await env.SeedAsync(env.Facilities, liveAi: true),
            [CsAgentBackfillClass.TooNew] = await env.SeedAsync(env.Facilities, created: Now.AddMinutes(-1)),
        };

        foreach (var (expected, ticket) in tickets)
        {
            Assert.Equal(expected, CsAgentBackfillClassifier.Classify(env.Evidence.Rows[ticket.TicketId], Now, env.Options));
        }

        // A live AI conversation that DID ask for a person is human work.
        var asked = await env.SeedAsync(env.Facilities, liveAi: true, humanFollowUp: true);
        Assert.Equal(CsAgentBackfillClass.Eligible, CsAgentBackfillClassifier.Classify(env.Evidence.Rows[asked.TicketId], Now, env.Options));

        // Final rule: an AI conversation that ENDED without resolving/closing the ticket gets a CS Agent by default (opt OUT only to hold it back).
        var aiEnded = await env.SeedAsync(env.Facilities, aiEnded: true);
        Assert.True(new CsAgentBackfillOptions().IncludeAiEndedWithoutHuman);
        Assert.Equal(CsAgentBackfillClass.Eligible, CsAgentBackfillClassifier.Classify(env.Evidence.Rows[aiEnded.TicketId], Now, env.Options));
        env.Options.IncludeAiEndedWithoutHuman = false;
        Assert.Equal(CsAgentBackfillClass.AiEndedNoHumanNeed, CsAgentBackfillClassifier.Classify(env.Evidence.Rows[aiEnded.TicketId], Now, env.Options));
        env.Options.IncludeAiEndedWithoutHuman = true;

        // A confirmed handling agent means a person is on it: eligible even inside a "live" conversation (never ActiveAiConversation).
        var handled = await env.SeedAsync(env.Facilities, liveAi: true, answeredBy: env.Agents[0]);
        Assert.Equal(CsAgentBackfillClass.Eligible, CsAgentBackfillClassifier.Classify(env.Evidence.Rows[handled.TicketId], Now, env.Options));

        // Closed / Resolved / owned are never eligible, whatever else is true of them.
        var closed = env.Evidence.Rows[tickets[CsAgentBackfillClass.Eligible].TicketId] with { Status = TicketStatus.Closed };
        var resolved = closed with { Status = TicketStatus.Resolved };
        var owned = closed with { Status = TicketStatus.Open, OwnerEmployeeId = Guid.NewGuid() };
        Assert.Equal(CsAgentBackfillClass.NotActive, CsAgentBackfillClassifier.Classify(closed, Now, env.Options));
        Assert.Equal(CsAgentBackfillClass.NotActive, CsAgentBackfillClassifier.Classify(resolved, Now, env.Options));
        Assert.Equal(CsAgentBackfillClass.HasOwner, CsAgentBackfillClassifier.Classify(owned, Now, env.Options));
    }

    // ---- preview: read-only, real cursor order -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Preview_ProposesAgentsInTheRealRotationOrder_ListsExclusions_AndWritesNothing()
    {
        var env = new Env(3);
        // The rotation is already part-way round: the cursor sits on agent 1, so the next agent is agent 2 - the backfill must NOT restart at agent 1.
        (await env.Rotation.GetForUpdateAsync(env.CallCenter.DepartmentId, Now)).Advance(env.Agents[0], Now);

        var t1 = await env.SeedAsync(env.Facilities, TicketStatus.Open);
        var t2 = await env.SeedAsync(env.Leasing, TicketStatus.InProgress);
        var ai = await env.SeedAsync(env.Facilities, liveAi: true);
        var t3 = await env.SeedAsync(env.Facilities, TicketStatus.Open);
        var approval = await env.SeedAsync(env.Leasing, pendingApproval: true);
        var t4 = await env.SeedAsync(env.Leasing, TicketStatus.Open);
        await env.SeedAsync(env.Facilities, owner: Guid.NewGuid());   // owned: never listed
        var service = env.Service();

        var preview = await service.PreviewAsync(0, 50, env.Options);

        Assert.True(preview.CallCenterConfigured);
        Assert.Equal(3, preview.PoolSize);
        Assert.Equal(env.Agents[0], preview.RotationCursor);
        Assert.Equal(6, preview.Examined);
        Assert.Equal(4, preview.Eligible);
        Assert.Equal(1, preview.ExclusionCounts[nameof(CsAgentBackfillClass.ActiveAiConversation)]);
        Assert.Equal(1, preview.ExclusionCounts[nameof(CsAgentBackfillClass.PendingApproval)]);
        Assert.Equal(2, preview.EligibleByDepartment["Facilities Management"]);                       // t1 + t3
        Assert.Equal(2, preview.EligibleByDepartment["Leasing"]);                                     // t2 + t4
        Assert.Equal(
            [t1.TicketId, t2.TicketId, ai.TicketId, t3.TicketId, approval.TicketId, t4.TicketId],
            preview.Rows.Select(r => r.TicketId));                                                    // oldest first
        Assert.Equal(
            [env.Agents[1], env.Agents[2], env.Agents[0], env.Agents[1]],
            preview.Rows.Where(r => r.Class == CsAgentBackfillClass.Eligible).Select(r => r.ProposedEmployeeId!.Value));
        Assert.All(preview.Rows.Where(r => r.Class != CsAgentBackfillClass.Eligible), r => Assert.Null(r.ProposedEmployeeId));
        Assert.Equal("Leasing", preview.Rows.Single(r => r.TicketId == t2.TicketId).DepartmentName);

        // Read-only: no write of any kind, and the persisted cursor did not move.
        Assert.Equal(0, env.UnitOfWork.SaveChangesCallCount);
        Assert.Empty(env.Assignments.Added);
        Assert.Empty(env.Audit.Entries);
        Assert.Empty(env.Events.All);
        Assert.Equal(env.Agents[0], env.Rotation.Rows[env.CallCenter.DepartmentId].LastAssignedEmployeeId);
    }

    // ---- execution: same engine, continues the cursor, preserves everything else -------------------------------------------------------

    [Fact]
    public async Task Backfill_AssignsOldestFirst_ContinuesTheRotation_AndChangesNothingButTheOwner()
    {
        var env = new Env(3);
        (await env.Rotation.GetForUpdateAsync(env.CallCenter.DepartmentId, Now)).Advance(env.Agents[0], Now);
        var tickets = new[]
        {
            await env.SeedAsync(env.Facilities, TicketStatus.Open),
            await env.SeedAsync(env.Leasing, TicketStatus.InProgress),
            await env.SeedAsync(env.Facilities, TicketStatus.Open),
            await env.SeedAsync(env.Leasing, TicketStatus.Open),
        };
        var before = tickets.Select(t => (t.CurrentDepartmentId, t.OriginatingDepartmentId, t.TicketStatus, t.PriorityId, t.CategoryId, t.RequestTypeId, t.SlaState, t.CreatedAtUtc, t.ReopenCount)).ToArray();

        foreach (var t in tickets)
        {
            var result = await env.Service().TryBackfillAsync(t.TicketId, "batch-1", env.Options, dryRun: false);
            Assert.Equal(CsAgentBackfillOutcome.Assigned, result.Outcome);
        }

        Assert.Equal([env.Agents[1], env.Agents[2], env.Agents[0], env.Agents[1]], tickets.Select(t => t.CurrentOwnerEmployeeId!.Value));
        Assert.Equal(before, tickets.Select(t => (t.CurrentDepartmentId, t.OriginatingDepartmentId, t.TicketStatus, t.PriorityId, t.CategoryId, t.RequestTypeId, t.SlaState, t.CreatedAtUtc, t.ReopenCount)).ToArray());
        Assert.Equal(env.Agents[1], env.Rotation.Rows[env.CallCenter.DepartmentId].LastAssignedEmployeeId);   // the one shared cursor advanced
        Assert.Single(env.Rotation.Rows);
        Assert.Equal(4, env.Assignments.Added.Count(a => a.IsCurrent));
        Assert.All(env.Assignments.Added, a => Assert.Null(a.AssigningActorEmployeeId));                       // a system action
        Assert.Equal(4, env.Audit.Entries.Count(e => e.Action == "BackfillAssign" && e.AfterValue!.Contains("BatchId=batch-1")));
        Assert.Equal(4, env.Audit.Entries.Count(e => e.Action == "AutoAssign" && e.AfterValue!.Contains("Trigger=Backfill")));
        Assert.Empty(env.Events.All);                                                                          // no transfer / status event
    }

    [Fact]
    public async Task Backfill_IsIdempotent_ASecondPassWritesNothing_AndNeverTouchesAnAssignedTicket()
    {
        var env = new Env(2);
        var ticket = await env.SeedAsync(env.Facilities);
        var otherOwner = Guid.NewGuid();
        var assigned = await env.SeedAsync(env.Leasing);
        assigned.AssignTo(otherOwner);   // assigned after listing: the stale evidence still says "unowned"

        var first = await env.Service().TryBackfillAsync(ticket.TicketId, "b", env.Options, false);
        var auditAfterFirst = env.Audit.Entries.Count;
        var assignmentsAfterFirst = env.Assignments.Added.Count;
        var again = await env.Service().TryBackfillAsync(ticket.TicketId, "b", env.Options, false);
        var raced = await env.Service().TryBackfillAsync(assigned.TicketId, "b", env.Options, false);

        Assert.Equal(CsAgentBackfillOutcome.Assigned, first.Outcome);
        Assert.Equal(CsAgentBackfillOutcome.NoLongerApplicable, again.Outcome);
        Assert.Equal(CsAgentBackfillOutcome.NoLongerApplicable, raced.Outcome);
        Assert.Equal(otherOwner, assigned.CurrentOwnerEmployeeId);                  // an existing owner is never replaced
        Assert.Equal(auditAfterFirst, env.Audit.Entries.Count);
        Assert.Equal(assignmentsAfterFirst, env.Assignments.Added.Count);
    }

    [Fact]
    public async Task Backfill_NeverAssigns_ExcludedTickets()
    {
        var env = new Env(2);
        var tickets = new[]
        {
            await env.SeedAsync(env.Facilities, liveAi: true),
            await env.SeedAsync(env.Facilities, pendingApproval: true),
            await env.SeedAsync(env.Facilities, aiEnded: true),   // excluded only because this test holds AI-ended tickets back
            await env.SeedAsync(env.Facilities, created: Now.AddSeconds(-30)),
        };
        env.Options.IncludeAiEndedWithoutHuman = false;

        foreach (var t in tickets)
        {
            Assert.Equal(CsAgentBackfillOutcome.Skipped, (await env.Service().TryBackfillAsync(t.TicketId, "b", env.Options, false)).Outcome);
            Assert.Null(t.CurrentOwnerEmployeeId);
        }

        Assert.Equal(0, env.UnitOfWork.SaveChangesCallCount);
        Assert.Empty(env.Assignments.Added);
        Assert.Empty(env.Rotation.Rows);   // the cursor was never even created
    }

    // ---- final business rules: confirmed handler, AI-ended, non-CS-agent handler ------------------------------------------------------

    [Fact]
    public async Task Backfill_GivesATicketToItsConfirmedHandlingCsAgent_WithoutMovingItOrAdvancingTheRotation()
    {
        var env = new Env(3);
        (await env.Rotation.GetForUpdateAsync(env.CallCenter.DepartmentId, Now)).Advance(env.Agents[0], Now);
        var handled = await env.SeedAsync(env.Facilities, answeredBy: env.Agents[2]);
        var plain = await env.SeedAsync(env.Leasing);
        var departmentBefore = handled.CurrentDepartmentId;

        var preview = await env.Service().PreviewAsync(0, 10, env.Options);
        var handledRow = preview.Rows.Single(r => r.TicketId == handled.TicketId);
        Assert.Equal(env.Agents[2], handledRow.ProposedEmployeeId);
        Assert.Equal(CsAgentBackfillSource.ConfirmedHandler, handledRow.ProposedSource);
        Assert.Equal(env.Agents[1], preview.Rows.Single(r => r.TicketId == plain.TicketId).ProposedEmployeeId);   // the rotation was not consumed by the handler

        var dry = await env.Service().TryBackfillAsync(handled.TicketId, "b", env.Options, dryRun: true);
        Assert.Equal(env.Agents[2], dry.AssignedEmployeeId);
        Assert.Null(handled.CurrentOwnerEmployeeId);

        var live = await env.Service().TryBackfillAsync(handled.TicketId, "b", env.Options, dryRun: false);
        var liveRr = await env.Service().TryBackfillAsync(plain.TicketId, "b", env.Options, dryRun: false);

        Assert.Equal(CsAgentBackfillOutcome.Assigned, live.Outcome);
        Assert.Equal(CsAgentBackfillSource.ConfirmedHandler, live.Source);
        Assert.Equal(env.Agents[2], handled.CurrentOwnerEmployeeId);
        Assert.Equal(departmentBefore, handled.CurrentDepartmentId);                                  // never moved to the Call Center
        Assert.Equal(env.Agents[1], plain.CurrentOwnerEmployeeId);                                    // cursor continued from agent 0, untouched by the handler
        Assert.Equal(CsAgentBackfillSource.RoundRobin, liveRr.Source);
        Assert.Contains(env.Audit.Entries, e => e.Action == "BackfillAssign" && e.AfterValue!.Contains("Source=ConfirmedHandler"));
        Assert.Empty(env.Events.All);
    }

    [Fact]
    public async Task Backfill_AHandlerWhoIsNotAnEligibleCsAgent_FallsBackToTheSharedRotation()
    {
        var env = new Env(2);
        var supervisor = env.AddAgent(Roles.DepartmentEmployee);   // a department employee in the pool's department: never given tickets automatically
        var outsider = Guid.NewGuid();                              // not a Call Center member at all
        var a = await env.SeedAsync(env.Facilities, answeredBy: supervisor);
        var b = await env.SeedAsync(env.Facilities, answeredBy: outsider);

        Assert.Equal(CsAgentBackfillOutcome.Assigned, (await env.Service().TryBackfillAsync(a.TicketId, "b", env.Options, false)).Outcome);
        Assert.Equal(CsAgentBackfillOutcome.Assigned, (await env.Service().TryBackfillAsync(b.TicketId, "b", env.Options, false)).Outcome);

        Assert.Equal([env.Agents[0], env.Agents[1]], new[] { a.CurrentOwnerEmployeeId!.Value, b.CurrentOwnerEmployeeId!.Value });
    }

    [Fact]
    public async Task Backfill_AssignsAiEndedTickets_ByDefault_ButNotALiveAiConversationOrAPendingApproval()
    {
        var env = new Env(2);
        var ended = await env.SeedAsync(env.Facilities, aiEnded: true);
        var live = await env.SeedAsync(env.Facilities, liveAi: true);
        var approval = await env.SeedAsync(env.Facilities, pendingApproval: true);

        Assert.Equal(CsAgentBackfillOutcome.Assigned, (await env.Service().TryBackfillAsync(ended.TicketId, "b", env.Options, false)).Outcome);
        Assert.Equal(CsAgentBackfillOutcome.Skipped, (await env.Service().TryBackfillAsync(live.TicketId, "b", env.Options, false)).Outcome);
        Assert.Equal(CsAgentBackfillOutcome.Skipped, (await env.Service().TryBackfillAsync(approval.TicketId, "b", env.Options, false)).Outcome);
        Assert.NotNull(ended.CurrentOwnerEmployeeId);
        Assert.Null(live.CurrentOwnerEmployeeId);
        Assert.Null(approval.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task Backfill_AfterAFullPass_NoEligibleTicketRemainsUnassigned_AndEveryOwnerIsACsAgent()
    {
        var env = new Env(3);
        var worked = new[]
        {
            await env.SeedAsync(env.Facilities, TicketStatus.Open),
            await env.SeedAsync(env.Leasing, TicketStatus.InProgress),
            await env.SeedAsync(env.Facilities, aiEnded: true),
            await env.SeedAsync(env.Leasing, answeredBy: env.Agents[1]),
        };
        var live = await env.SeedAsync(env.Facilities, liveAi: true);          // intentionally excluded
        var approval = await env.SeedAsync(env.Leasing, pendingApproval: true);    // intentionally excluded

        foreach (var row in (await env.Service().PreviewAsync(0, 50, env.Options)).Rows.Where(r => r.Class == CsAgentBackfillClass.Eligible))
        {
            Assert.Equal(CsAgentBackfillOutcome.Assigned, (await env.Service().TryBackfillAsync(row.TicketId, "b", env.Options, false)).Outcome);
        }

        Assert.All(worked, t => Assert.Contains(t.CurrentOwnerEmployeeId!.Value, env.Agents));   // every owner is a CS Agent, never a department employee
        Assert.Equal(env.Agents[1], worked[3].CurrentOwnerEmployeeId);                           // the confirmed handler kept the ticket
        Assert.Null(live.CurrentOwnerEmployeeId);
        Assert.Null(approval.CurrentOwnerEmployeeId);
        Assert.Equal(env.Facilities.DepartmentId, worked[0].CurrentDepartmentId);                // department untouched
    }

    [Fact]
    public async Task DryRun_ReportsWouldAssign_AndWritesNothing()
    {
        var env = new Env(2);
        var ticket = await env.SeedAsync(env.Facilities);

        var result = await env.Service().TryBackfillAsync(ticket.TicketId, "b", env.Options, dryRun: true);

        Assert.Equal(CsAgentBackfillOutcome.WouldAssign, result.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(0, env.UnitOfWork.TransactionsBegun);
        Assert.Empty(env.Audit.Entries);
        Assert.Empty(env.Rotation.Rows);
    }

    [Fact]
    public async Task Backfill_WithNoEligibleAgent_RecordsTheFailureOnce_AndAssignsNobody()
    {
        var env = new Env(0);
        env.AddAgent(Roles.DepartmentEmployee);   // not a CS Agent
        var ticket = await env.SeedAsync(env.Facilities);

        var first = await env.Service().TryBackfillAsync(ticket.TicketId, "b", env.Options, false);
        var second = await env.Service().TryBackfillAsync(ticket.TicketId, "b", env.Options, false);

        Assert.Equal(CsAgentBackfillOutcome.NoEligibleAgent, first.Outcome);
        Assert.Equal(CsAgentBackfillOutcome.NoEligibleAgent, second.Outcome);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Single(env.Events.All, e => e.EventType == WorkflowEventType.ManualAssignmentRequired);   // deduplicated
        Assert.Empty(env.Assignments.Added);
    }

    [Fact]
    public async Task Backfill_ALostRace_IsReported_NotThrown_AndTheNextTicketStillGetsTheNextAgent()
    {
        var env = new Env(2);
        var t1 = await env.SeedAsync(env.Facilities);
        var t2 = await env.SeedAsync(env.Leasing);
        env.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = 1;   // another writer changed t1 first

        var lost = await env.Service().TryBackfillAsync(t1.TicketId, "b", env.Options, false);

        Assert.Equal(CsAgentBackfillOutcome.LostRace, lost.Outcome);
        Assert.Equal(1, env.UnitOfWork.TransactionsBegun);
        Assert.Equal(0, env.UnitOfWork.TransactionsCommitted);   // rolled back, nothing committed
    }

    // ---- runner: throttled batches, resume, gates ---------------------------------------------------------------------------------------

    private sealed class FakeLock(bool busy = false) : ICallCenterReconciliationRunLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable?>(busy ? null : new Handle());
        private sealed class Handle : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    private static CsAgentBackfillRunner Runner(Env env, bool lockBusy = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITicketUnassignedWorkRepository>(env.Evidence);
        services.AddSingleton<ITicketRepository>(env.Tickets);
        services.AddSingleton<IDepartmentRepository>(env.Departments);
        services.AddSingleton<IUserDepartmentAssignmentRepository>(env.Members);
        services.AddSingleton<ITicketAssignmentRepository>(env.Assignments);
        services.AddSingleton<IAuditEntryWriter>(env.Audit);
        services.AddSingleton<ITicketingUnitOfWork>(env.UnitOfWork);
        services.AddSingleton<ITicketWorkflowEventRepository>(env.Events);
        services.AddSingleton<IAssignmentRotationRepository>(env.Rotation);
        services.AddSingleton<IRequestTypeAssignmentRuleRepository>(new FakeRequestTypeAssignmentRuleRepository());
        services.AddSingleton<IDepartmentWorkflowSettingsRepository>(new FakeDepartmentWorkflowSettingsRepository());
        services.AddSingleton<IUserRoleReader>(env.Accounts);
        services.AddSingleton<IUserAccountManager>(env.Accounts);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        services.AddScoped<TicketAutoAssignmentService>();
        services.AddScoped<CsAgentBackfillService>();
        var provider = services.BuildServiceProvider();
        return new CsAgentBackfillRunner(
            provider.GetRequiredService<IServiceScopeFactory>(), new StaticOptions(env.Options), new FakeLock(lockBusy), NullLogger<CsAgentBackfillRunner>.Instance);
    }

    private sealed class StaticOptions(CsAgentBackfillOptions value) : IOptionsMonitor<CsAgentBackfillOptions>
    {
        public CsAgentBackfillOptions CurrentValue => value;
        public CsAgentBackfillOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<CsAgentBackfillOptions, string?> listener) => null;
    }

    [Fact]
    public async Task Runner_LiveRun_IsRefusedUnlessExplicitlyEnabled_ButDryRunAlwaysWorks()
    {
        var env = new Env(2);
        env.Options.Enabled = false;
        var ticket = await env.SeedAsync(env.Facilities);
        var runner = Runner(env);

        var live = await runner.RunAsync(new CsAgentBackfillRunRequest(DryRun: false), default);
        var dry = await runner.RunAsync(new CsAgentBackfillRunRequest(DryRun: true), default);

        Assert.Equal(CsAgentBackfillRunStatus.Disabled, live.Status);
        Assert.Equal(CsAgentBackfillRunStatus.Completed, dry.Status);
        Assert.Equal(1, dry.WouldAssign);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(0, env.UnitOfWork.SaveChangesCallCount);
    }

    [Fact]
    public async Task Runner_ThrottlesIntoBatches_ResumesWhereItStopped_AndNeverResetsTheRotation()
    {
        var env = new Env(3);
        var tickets = new List<Ticket>();
        for (var i = 0; i < 7; i++)
        {
            tickets.Add(await env.SeedAsync(i % 2 == 0 ? env.Facilities : env.Leasing));
        }

        var runner = Runner(env);
        var after = 0L;
        var batches = new List<CsAgentBackfillRunResult>();
        do
        {
            var batch = await runner.RunAsync(new CsAgentBackfillRunRequest(DryRun: false, BatchSize: 3, AfterTicketId: after, BatchId: "bf-1"), default);
            batches.Add(batch);
            after = batch.ResumeAfterTicketId;
        }
        while (!batches[^1].BacklogExhausted);

        Assert.Equal([3, 3, 1], batches.Select(b => b.Examined));                                  // never more than the batch size per call
        Assert.All(batches, b => Assert.Equal(CsAgentBackfillRunStatus.Completed, b.Status));
        Assert.Equal(7, batches.Sum(b => b.Assigned));
        // One continuous rotation across batches, oldest first: 1,2,3,1,2,3,1.
        Assert.Equal(
            [env.Agents[0], env.Agents[1], env.Agents[2], env.Agents[0], env.Agents[1], env.Agents[2], env.Agents[0]],
            tickets.Select(t => t.CurrentOwnerEmployeeId!.Value));

        // Recovery: running everything again from 0 (e.g. after an interrupted run) assigns nothing. (The stub evidence is static, so the tickets are
        // still listed; the real query no longer returns owned tickets - see the SQLite test - and the under-transaction re-check rejects them anyway.)
        var rerun = await runner.RunAsync(new CsAgentBackfillRunRequest(DryRun: false, BatchSize: 50), default);
        Assert.Equal(0, rerun.Assigned);
        Assert.Equal(7, rerun.NoLongerApplicable);
        Assert.Equal(7, env.Assignments.Added.Count);
        Assert.Equal(7, env.Audit.Entries.Count(e => e.Action == "BackfillAssign"));
    }

    [Fact]
    public async Task Runner_ReportsExclusions_AndNeverExceedsTheConfiguredMaxBatchSize()
    {
        var env = new Env(2);
        env.Options.MaxBatchSize = 4;
        for (var i = 0; i < 3; i++) await env.SeedAsync(env.Facilities);
        await env.SeedAsync(env.Leasing, liveAi: true);
        await env.SeedAsync(env.Leasing, pendingApproval: true);
        await env.SeedAsync(env.Leasing);

        var result = await Runner(env).RunAsync(new CsAgentBackfillRunRequest(DryRun: true, BatchSize: 1000), default);

        Assert.Equal(4, result.Examined);                       // clamped to MaxBatchSize
        Assert.Equal(3, result.WouldAssign);
        Assert.Equal(1, result.Excluded[nameof(CsAgentBackfillClass.ActiveAiConversation)]);
        Assert.False(result.BacklogExhausted);
    }

    [Fact]
    public async Task Runner_RefusesALiveRunWithNoEligibleAgent_BeforeTouchingAnyTicket_AndWhenAnotherRunHoldsTheLock()
    {
        var empty = new Env(0);
        var ticket = await empty.SeedAsync(empty.Facilities);
        var none = await Runner(empty).RunAsync(new CsAgentBackfillRunRequest(DryRun: false), default);
        Assert.Equal(CsAgentBackfillRunStatus.NoEligiblePool, none.Status);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Empty(empty.Events.All);                         // no failure events stamped on the whole backlog
        Assert.Equal(0, empty.UnitOfWork.SaveChangesCallCount);

        var env = new Env(2);
        await env.SeedAsync(env.Facilities);
        var busy = await Runner(env, lockBusy: true).RunAsync(new CsAgentBackfillRunRequest(DryRun: false), default);
        Assert.Equal(CsAgentBackfillRunStatus.Busy, busy.Status);
        Assert.Equal(0, env.UnitOfWork.SaveChangesCallCount);
    }

    // ---- stale AI conversations ---------------------------------------------------------------------------------------------------------

    private static CsAgentBackfillOptions Stale(int? hours) => new() { Enabled = true, DelayBetweenTicketsMs = 0, StaleAiAfterHours = hours };

    [Fact]
    public async Task StaleAi_IsDisabledByDefault_SoEveryLiveAiConversationStaysProtected_HoweverOld()
    {
        Assert.Null(new CsAgentBackfillOptions().StaleAiAfterHours);
        Assert.Null(new CsAgentBackfillOptions().StaleAiThreshold);

        var env = new Env();
        var ancient = await env.SeedAsync(env.Facilities, liveAi: true, lastActivity: Now.AddDays(-90));

        foreach (var hours in new int?[] { null, 0, -5 })   // null, zero and negative all mean "off"
        {
            Assert.Equal(CsAgentBackfillClass.ActiveAiConversation, CsAgentBackfillClassifier.Classify(env.Evidence.Rows[ancient.TicketId], Now, Stale(hours)));
        }

        Assert.Equal(CsAgentBackfillOutcome.Skipped, (await env.Service().TryBackfillAsync(ancient.TicketId, "b", Stale(null), dryRun: false)).Outcome);
        Assert.Null(ancient.CurrentOwnerEmployeeId);
    }

    [Theory]
    [InlineData(12, 11.9, false)]
    [InlineData(12, 12.0, false)]    // exactly the threshold is NOT "more than"
    [InlineData(12, 12.1, true)]
    [InlineData(24, 23.9, false)]
    [InlineData(24, 24.0, false)]
    [InlineData(24, 24.1, true)]
    [InlineData(48, 47.9, false)]
    [InlineData(48, 48.1, true)]
    [InlineData(24, 24 * 30.0, true)]
    public async Task StaleAi_BecomesEligibleOnlyAfterSilenceLongerThanTheThreshold(int threshold, double silentHours, bool eligible)
    {
        var env = new Env();
        var ticket = await env.SeedAsync(env.Facilities, liveAi: true, lastActivity: Now.AddHours(-silentHours));

        var cls = CsAgentBackfillClassifier.Classify(env.Evidence.Rows[ticket.TicketId], Now, Stale(threshold));

        Assert.Equal(eligible ? CsAgentBackfillClass.Eligible : CsAgentBackfillClass.ActiveAiConversation, cls);
        Assert.Equal(eligible, CsAgentBackfillClassifier.IsStaleAi(env.Evidence.Rows[ticket.TicketId], Now, Stale(threshold)));
    }

    [Fact]
    public async Task StaleAi_ProtectsPendingApprovals_AndCreatedMoments_AndEndedOrClosedTickets()
    {
        var env = new Env();
        var approval = await env.SeedAsync(env.Facilities, liveAi: true, pendingApproval: true, lastActivity: Now.AddDays(-5));
        var tooNew = await env.SeedAsync(env.Facilities, liveAi: true, created: Now.AddMinutes(-1), lastActivity: Now.AddDays(-5));
        var options = Stale(24);

        Assert.Equal(CsAgentBackfillClass.PendingApproval, CsAgentBackfillClassifier.Classify(env.Evidence.Rows[approval.TicketId], Now, options));
        Assert.Equal(CsAgentBackfillClass.TooNew, CsAgentBackfillClassifier.Classify(env.Evidence.Rows[tooNew.TicketId], Now, options));

        var closed = env.Evidence.Rows[approval.TicketId] with { PendingApproval = false, Status = TicketStatus.Closed };
        Assert.Equal(CsAgentBackfillClass.NotActive, CsAgentBackfillClassifier.Classify(closed, Now, options));
        var owned = env.Evidence.Rows[approval.TicketId] with { PendingApproval = false, OwnerEmployeeId = Guid.NewGuid() };
        Assert.Equal(CsAgentBackfillClass.HasOwner, CsAgentBackfillClassifier.Classify(owned, Now, options));
    }

    [Fact]
    public async Task OpenHandoff_AndHumanFollowUp_AlwaysGetACsAgent_EvenWhenRecent_AndWithTheStaleRuleOff()
    {
        var env = new Env(2);
        var handoff = await env.SeedAsync(env.Facilities, liveAi: true, openHandoff: true, lastActivity: Now.AddMinutes(-2));
        var followUp = await env.SeedAsync(env.Facilities, liveAi: true, humanFollowUp: true, lastActivity: Now.AddMinutes(-2));

        foreach (var options in new[] { Stale(null), Stale(24) })
        {
            Assert.Equal(CsAgentBackfillClass.Eligible, CsAgentBackfillClassifier.Classify(env.Evidence.Rows[handoff.TicketId], Now, options));
            Assert.Equal(CsAgentBackfillClass.Eligible, CsAgentBackfillClassifier.Classify(env.Evidence.Rows[followUp.TicketId], Now, options));
            // Eligible because a person is required, not because it is stale.
            Assert.False(CsAgentBackfillClassifier.IsStaleAi(env.Evidence.Rows[handoff.TicketId], Now, options));
        }

        Assert.Equal(CsAgentBackfillOutcome.Assigned, (await env.Service().TryBackfillAsync(handoff.TicketId, "b", Stale(null), dryRun: false)).Outcome);
        Assert.Equal(CsAgentBackfillOutcome.Assigned, (await env.Service().TryBackfillAsync(followUp.TicketId, "b", Stale(null), dryRun: false)).Outcome);
        Assert.NotNull(handoff.CurrentOwnerEmployeeId);
        Assert.NotNull(followUp.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task StaleAi_UsesTheSharedRotation_PreservesEverythingButTheOwner_AndAuditsTheReason()
    {
        var env = new Env(3);
        (await env.Rotation.GetForUpdateAsync(env.CallCenter.DepartmentId, Now)).Advance(env.Agents[0], Now);
        var stale = await env.SeedAsync(env.Facilities, liveAi: true, lastActivity: Now.AddHours(-30));
        var recent = await env.SeedAsync(env.Facilities, liveAi: true, lastActivity: Now.AddHours(-2));
        var before = (stale.CurrentDepartmentId, stale.OriginatingDepartmentId, stale.TicketStatus, stale.PriorityId, stale.CategoryId, stale.RequestTypeId, stale.SlaState, stale.CreatedAtUtc, stale.ReopenCount);
        var options = Stale(24);

        var result = await env.Service().TryBackfillAsync(stale.TicketId, "stale-1", options, dryRun: false);
        var recentResult = await env.Service().TryBackfillAsync(recent.TicketId, "stale-1", options, dryRun: false);

        Assert.Equal(CsAgentBackfillOutcome.Assigned, result.Outcome);
        Assert.True(result.StaleAi);
        Assert.Equal(CsAgentBackfillSource.RoundRobin, result.Source);
        Assert.Equal(env.Agents[1], stale.CurrentOwnerEmployeeId);                                   // the shared cursor continued (agent 0 was last)
        Assert.Equal(env.Agents[1], env.Rotation.Rows[env.CallCenter.DepartmentId].LastAssignedEmployeeId);
        Assert.Equal(before, (stale.CurrentDepartmentId, stale.OriginatingDepartmentId, stale.TicketStatus, stale.PriorityId, stale.CategoryId, stale.RequestTypeId, stale.SlaState, stale.CreatedAtUtc, stale.ReopenCount));
        Assert.Equal(CsAgentBackfillOutcome.Skipped, recentResult.Outcome);                          // a recent AI conversation stays protected
        Assert.Null(recent.CurrentOwnerEmployeeId);
        Assert.Single(env.Assignments.Added);
        Assert.Contains(env.Audit.Entries, e => e.Action == "BackfillAssign" && e.AfterValue!.Contains("Reason=StaleAi") && e.AfterValue.Contains("BatchId=stale-1"));
        Assert.Empty(env.Events.All);                                                                // no transfer / status event: history untouched
    }

    [Fact]
    public async Task StaleAi_PreviewAndRun_AgreeTicketForTicket()
    {
        var env = new Env(3);
        await env.SeedAsync(env.Facilities, liveAi: true, lastActivity: Now.AddHours(-13));       // stale at 12h only
        await env.SeedAsync(env.Facilities, liveAi: true, lastActivity: Now.AddHours(-30));       // stale at 12h and 24h
        await env.SeedAsync(env.Leasing, liveAi: true, lastActivity: Now.AddHours(-60));          // stale at all three
        await env.SeedAsync(env.Leasing, liveAi: true, lastActivity: Now.AddMinutes(-20));        // never stale
        await env.SeedAsync(env.Leasing, liveAi: true, pendingApproval: true, lastActivity: Now.AddDays(-9));
        await env.SeedAsync(env.Facilities, liveAi: true, openHandoff: true, lastActivity: Now.AddMinutes(-5));

        var counts = new Dictionary<int, int>();
        foreach (var hours in new[] { 12, 24, 48 })
        {
            var options = Stale(hours);
            var preview = await env.Service().PreviewAsync(0, 50, options);
            var dry = new List<CsAgentBackfillTicketResult>();
            foreach (var row in preview.Rows)
            {
                dry.Add(await env.Service().TryBackfillAsync(row.TicketId, "b", options, dryRun: true));
            }

            // The same classification, ticket by ticket, for Preview and for the run's own re-check.
            Assert.Equal(
                preview.Rows.Select(r => (r.TicketId, Eligible: r.Class == CsAgentBackfillClass.Eligible, r.StaleAi)),
                dry.Select(d => (d.TicketId, Eligible: d.Outcome == CsAgentBackfillOutcome.WouldAssign, d.StaleAi)));
            counts[hours] = preview.Rows.Count(r => r.StaleAi);
        }

        Assert.Equal(new Dictionary<int, int> { [12] = 3, [24] = 2, [48] = 1 }, counts);
    }

    [Fact]
    public async Task StaleAi_IsRecheckedDuringExecution_ANewCustomerMessageProtectsTheTicketAgain()
    {
        var env = new Env(2);
        var ticket = await env.SeedAsync(env.Facilities, liveAi: true, lastActivity: Now.AddHours(-30));
        var options = Stale(24);
        var preview = await env.Service().PreviewAsync(0, 10, options);
        Assert.True(preview.Rows.Single().StaleAi);                                                  // eligible at preview time

        // The customer writes after the preview / listing and before the write: the first read still says stale, the re-check under the transaction does not.
        var racing = new ActivityArrivesAfterFirstRead(env.Evidence, ticket.TicketId, Now.AddMinutes(-1));
        var service = new CsAgentBackfillService(racing, env.Tickets, env.Engine(), env.Departments, env.Audit, env.UnitOfWork, new FakeTimeProvider(Now));

        var result = await service.TryBackfillAsync(ticket.TicketId, "b", options, dryRun: false);

        Assert.Equal(CsAgentBackfillOutcome.Skipped, result.Outcome);
        Assert.Equal(CsAgentBackfillClass.ActiveAiConversation, result.Class);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Empty(env.Assignments.Added);
        Assert.Empty(env.Audit.Entries);
    }

    [Fact]
    public async Task StaleAi_AConfirmedEligibleHandlingAgent_IsUsedInsteadOfTheRotation()
    {
        var env = new Env(3);
        (await env.Rotation.GetForUpdateAsync(env.CallCenter.DepartmentId, Now)).Advance(env.Agents[0], Now);
        var handled = await env.SeedAsync(env.Facilities, liveAi: true, answeredBy: env.Agents[2], lastActivity: Now.AddDays(-4));

        var result = await env.Service().TryBackfillAsync(handled.TicketId, "b", Stale(24), dryRun: false);

        Assert.Equal(CsAgentBackfillOutcome.Assigned, result.Outcome);
        Assert.Equal(CsAgentBackfillSource.ConfirmedHandler, result.Source);
        Assert.Equal(env.Agents[2], handled.CurrentOwnerEmployeeId);
        Assert.Equal(env.Agents[0], env.Rotation.Rows[env.CallCenter.DepartmentId].LastAssignedEmployeeId);   // the rotation did not move
    }

    // ---- approved rule: stale Genesys PHONE conversations (> 6h without activity, no end event needed) -----------------------------------

    private static CsAgentBackfillOptions PhoneRule(int? hours = 6) =>
        new() { Enabled = true, DelayBetweenTicketsMs = 0, StaleAiAfterHours = null, StalePhoneAiAfterHours = hours };

    [Fact]
    public void StalePhone_ApprovedThresholdIsSixHours_AndCanBeSwitchedOff()
    {
        Assert.Equal(6, new CsAgentBackfillOptions().StalePhoneAiAfterHours);
        Assert.Equal(TimeSpan.FromHours(6), new CsAgentBackfillOptions().StalePhoneAiThreshold);
        Assert.Null(new CsAgentBackfillOptions().StaleAiAfterHours);   // the generic all-channel rule stays off
        foreach (var off in new int?[] { null, 0, -1 })
        {
            Assert.Null(PhoneRule(off).StalePhoneAiThreshold);
        }

        Assert.Equal(6, new StalePhoneAssignmentOptions().StaleAfterHours);
        Assert.False(new StalePhoneAssignmentOptions().Enabled);   // the background process is off ...
        Assert.True(new StalePhoneAssignmentOptions().DryRun);     // ... and dry-run until approved
    }

    [Theory]
    [InlineData(5.9, false)]
    [InlineData(6.0, false)]   // exactly the threshold is NOT "more than"
    [InlineData(6.1, true)]
    [InlineData(48.0, true)]
    public async Task StalePhone_ReleasesAPhoneTicket_OnlyAfterMoreThanSixHoursOfSilence(double silentHours, bool eligible)
    {
        var env = new Env();
        var ticket = await env.SeedAsync(env.Facilities, liveAi: true, phone: true, lastActivity: Now.AddHours(-silentHours));
        var evidence = env.Evidence.Rows[ticket.TicketId];

        Assert.Equal(eligible ? CsAgentBackfillClass.Eligible : CsAgentBackfillClass.ActiveAiConversation, CsAgentBackfillClassifier.Classify(evidence, Now, PhoneRule()));
        Assert.Equal(eligible, CsAgentBackfillClassifier.IsStalePhoneAi(evidence, Now, PhoneRule()));
        Assert.Equal(CsAgentBackfillClass.ActiveAiConversation, CsAgentBackfillClassifier.Classify(evidence, Now, PhoneRule(null)));   // rule off: protected however old
    }

    [Fact]
    public async Task StalePhone_NonPhoneConversations_StayProtected_HoweverOld()
    {
        var env = new Env();
        var chat = await env.SeedAsync(env.Facilities, liveAi: true, phone: false, lastActivity: Now.AddDays(-30));      // live chat / messaging
        var mixed = env.Evidence.Rows[chat.TicketId] with { LiveGenesysIsPhoneOnly = false };                           // a live Phone call AND a live chat

        Assert.Equal(CsAgentBackfillClass.ActiveAiConversation, CsAgentBackfillClassifier.Classify(env.Evidence.Rows[chat.TicketId], Now, PhoneRule()));
        Assert.Equal(CsAgentBackfillClass.ActiveAiConversation, CsAgentBackfillClassifier.Classify(mixed, Now, PhoneRule()));
        Assert.False(CsAgentBackfillClassifier.IsStalePhoneAi(mixed, Now, PhoneRule()));
    }

    [Fact]
    public async Task StalePhone_OnlyOpenAndInProgress_NeverOwned_NeverPendingApproval_NeverTooNew_NeverClosed()
    {
        var env = new Env();
        var old = Now.AddHours(-30);
        var open = await env.SeedAsync(env.Facilities, TicketStatus.Open, liveAi: true, phone: true, lastActivity: old);
        var inProgress = await env.SeedAsync(env.Facilities, TicketStatus.InProgress, liveAi: true, phone: true, lastActivity: old);
        var approval = await env.SeedAsync(env.Facilities, liveAi: true, phone: true, pendingApproval: true, lastActivity: old);
        var tooNew = await env.SeedAsync(env.Facilities, liveAi: true, phone: true, created: Now.AddMinutes(-1), lastActivity: old);

        Assert.Equal(CsAgentBackfillClass.Eligible, CsAgentBackfillClassifier.Classify(env.Evidence.Rows[open.TicketId], Now, PhoneRule()));
        Assert.Equal(CsAgentBackfillClass.Eligible, CsAgentBackfillClassifier.Classify(env.Evidence.Rows[inProgress.TicketId], Now, PhoneRule()));
        Assert.Equal(CsAgentBackfillClass.PendingApproval, CsAgentBackfillClassifier.Classify(env.Evidence.Rows[approval.TicketId], Now, PhoneRule()));
        Assert.Equal(CsAgentBackfillClass.TooNew, CsAgentBackfillClassifier.Classify(env.Evidence.Rows[tooNew.TicketId], Now, PhoneRule()));

        var template = env.Evidence.Rows[open.TicketId];
        foreach (var status in new[] { TicketStatus.PendingCustomer, TicketStatus.PendingThirdParty })
        {
            var pending = template with { Status = status };
            Assert.Equal(CsAgentBackfillClass.ActiveAiConversation, CsAgentBackfillClassifier.Classify(pending, Now, PhoneRule()));   // not Open / InProgress: protected
            Assert.False(CsAgentBackfillClassifier.IsStalePhoneAi(pending, Now, PhoneRule()));
        }

        Assert.Equal(CsAgentBackfillClass.NotActive, CsAgentBackfillClassifier.Classify(template with { Status = TicketStatus.Closed }, Now, PhoneRule()));
        Assert.Equal(CsAgentBackfillClass.NotActive, CsAgentBackfillClassifier.Classify(template with { Status = TicketStatus.Resolved }, Now, PhoneRule()));
        var owned = template with { OwnerEmployeeId = Guid.NewGuid() };
        Assert.Equal(CsAgentBackfillClass.HasOwner, CsAgentBackfillClassifier.Classify(owned, Now, PhoneRule()));
        Assert.False(CsAgentBackfillClassifier.IsStalePhoneAi(owned, Now, PhoneRule()));
    }

    [Fact]
    public async Task StalePhone_AssignsThroughTheSharedRotation_AndChangesNothingButTheOwner()
    {
        var env = new Env(3);
        (await env.Rotation.GetForUpdateAsync(env.CallCenter.DepartmentId, Now)).Advance(env.Agents[0], Now);
        var ticket = await env.SeedAsync(env.Leasing, TicketStatus.InProgress, liveAi: true, phone: true, lastActivity: Now.AddHours(-9));
        var before = (ticket.CurrentDepartmentId, ticket.OriginatingDepartmentId, ticket.TicketStatus, ticket.CategoryId, ticket.RequestTypeId, ticket.PriorityId);
        var evidenceBefore = env.Evidence.Rows[ticket.TicketId];

        var result = await env.Service().TryBackfillAsync(ticket.TicketId, "phone-1", PhoneRule(), dryRun: false);

        Assert.Equal(CsAgentBackfillOutcome.Assigned, result.Outcome);
        Assert.True(result.StalePhoneAi);
        Assert.True(result.StaleAi);
        Assert.Equal(CsAgentBackfillSource.RoundRobin, result.Source);
        Assert.Equal(env.Agents[1], ticket.CurrentOwnerEmployeeId);                                              // next agent after the live cursor
        Assert.Equal(env.Agents[1], env.Rotation.Rows[env.CallCenter.DepartmentId].LastAssignedEmployeeId);
        Assert.Equal(before, (ticket.CurrentDepartmentId, ticket.OriginatingDepartmentId, ticket.TicketStatus, ticket.CategoryId, ticket.RequestTypeId, ticket.PriorityId));
        Assert.Equal(evidenceBefore, env.Evidence.Rows[ticket.TicketId]);                                        // no end timestamp / interaction change fabricated
        Assert.True(env.Evidence.Rows[ticket.TicketId].HasLiveGenesysInteraction);
        Assert.Single(env.Assignments.Added);
        Assert.Contains(env.Audit.Entries, e => e.Action == "BackfillAssign" && e.AfterValue!.Contains("Reason=StalePhoneAi") && e.AfterValue.Contains("BatchId=phone-1"));
        Assert.Empty(env.Events.All);                                                                            // no transfer / status / close event: history untouched
    }

    [Fact]
    public async Task StalePhone_NeverReassignsAnOwnedTicket_AndRecheckProtectsATicketThatBecameActive()
    {
        var env = new Env(2);
        var existing = env.Agents[1];
        var owned = await env.SeedAsync(env.Facilities, liveAi: true, phone: true, owner: existing, lastActivity: Now.AddDays(-2));
        Assert.NotEqual(CsAgentBackfillOutcome.Assigned, (await env.Service().TryBackfillAsync(owned.TicketId, "b", PhoneRule(), dryRun: false)).Outcome);
        Assert.Equal(existing, owned.CurrentOwnerEmployeeId);
        Assert.Empty(env.Assignments.Added);

        var ticket = await env.SeedAsync(env.Facilities, liveAi: true, phone: true, lastActivity: Now.AddHours(-12));
        var racing = new ActivityArrivesAfterFirstRead(env.Evidence, ticket.TicketId, Now.AddMinutes(-1));
        var service = new CsAgentBackfillService(racing, env.Tickets, env.Engine(), env.Departments, env.Audit, env.UnitOfWork, new FakeTimeProvider(Now));

        var result = await service.TryBackfillAsync(ticket.TicketId, "b", PhoneRule(), dryRun: false);

        Assert.Equal(CsAgentBackfillOutcome.Skipped, result.Outcome);                                           // a call that became active again is protected again
        Assert.Equal(CsAgentBackfillClass.ActiveAiConversation, result.Class);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task StalePhone_APhoneTicketWithHumanEvidence_IsHumanWork_NotTheStaleRule()
    {
        var env = new Env();
        var ticket = await env.SeedAsync(env.Facilities, liveAi: true, phone: true, humanEvidence: true, lastActivity: Now.AddMinutes(-5));
        var evidence = env.Evidence.Rows[ticket.TicketId];

        Assert.Equal(CsAgentBackfillClass.Eligible, CsAgentBackfillClassifier.Classify(evidence, Now, PhoneRule()));   // eligible as human work, however recent ...
        Assert.False(CsAgentBackfillClassifier.IsStalePhoneAi(evidence, Now, PhoneRule()));                           // ... and not reported as stale
    }

    [Fact]
    public async Task StalePhone_PreviewAndDryRun_ShowEligibleTicketsAndTheDistribution_WithoutWriting()
    {
        var env = new Env(3);
        for (var i = 0; i < 4; i++) await env.SeedAsync(env.Facilities, liveAi: true, phone: true, lastActivity: Now.AddHours(-7 - i));
        await env.SeedAsync(env.Leasing, liveAi: true, phone: true, lastActivity: Now.AddHours(-2));           // recent: protected
        await env.SeedAsync(env.Leasing, liveAi: true, phone: false, lastActivity: Now.AddDays(-5));           // chat: protected

        var preview = await env.Service().PreviewAsync(0, 50, PhoneRule());

        Assert.Equal(4, preview.Eligible);
        Assert.Equal(2, preview.ExclusionCounts[nameof(CsAgentBackfillClass.ActiveAiConversation)]);
        Assert.Equal(4, preview.Rows.Count(r => r.StalePhoneAi));
        var perAgent = preview.Rows.Where(r => r.ProposedEmployeeId is not null).GroupBy(r => r.ProposedEmployeeId!.Value).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(3, perAgent.Count);                                                                       // dealt across the pool
        Assert.Equal(4, perAgent.Values.Sum());
        Assert.Empty(env.Assignments.Added);
        Assert.Empty(env.Audit.Entries);
        Assert.Equal(0, env.UnitOfWork.SaveChangesCallCount);
    }

    // ---- the recurring background process -------------------------------------------------------------------------------------------------

    private sealed class StaticPhoneOptions(StalePhoneAssignmentOptions value) : IOptionsMonitor<StalePhoneAssignmentOptions>
    {
        public StalePhoneAssignmentOptions CurrentValue => value;
        public StalePhoneAssignmentOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StalePhoneAssignmentOptions, string?> listener) => null;
    }

    private static StalePhoneAssignmentJob PhoneJob(Env env, StalePhoneAssignmentOptions settings, bool lockBusy = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITicketUnassignedWorkRepository>(env.Evidence);
        services.AddSingleton<ITicketRepository>(env.Tickets);
        services.AddSingleton<IDepartmentRepository>(env.Departments);
        services.AddSingleton<IUserDepartmentAssignmentRepository>(env.Members);
        services.AddSingleton<ITicketAssignmentRepository>(env.Assignments);
        services.AddSingleton<IAuditEntryWriter>(env.Audit);
        services.AddSingleton<ITicketingUnitOfWork>(env.UnitOfWork);
        services.AddSingleton<ITicketWorkflowEventRepository>(env.Events);
        services.AddSingleton<IAssignmentRotationRepository>(env.Rotation);
        services.AddSingleton<IRequestTypeAssignmentRuleRepository>(new FakeRequestTypeAssignmentRuleRepository());
        services.AddSingleton<IDepartmentWorkflowSettingsRepository>(new FakeDepartmentWorkflowSettingsRepository());
        services.AddSingleton<IUserRoleReader>(env.Accounts);
        services.AddSingleton<IUserAccountManager>(env.Accounts);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        services.AddScoped<TicketAutoAssignmentService>();
        services.AddScoped<CsAgentBackfillService>();
        var provider = services.BuildServiceProvider();
        return new StalePhoneAssignmentJob(
            provider.GetRequiredService<IServiceScopeFactory>(), new StaticPhoneOptions(settings), new FakeLock(lockBusy), new FakeTimeProvider(Now),
            NullLogger<StalePhoneAssignmentJob>.Instance);
    }

    [Fact]
    public async Task StalePhoneJob_IsOffByDefault_AndDryRunWritesNothing()
    {
        var env = new Env(3);
        await env.SeedAsync(env.Facilities, liveAi: true, phone: true, lastActivity: Now.AddHours(-10));

        var off = await PhoneJob(env, new StalePhoneAssignmentOptions()).RunAsync(default);
        Assert.Equal(StalePhoneAssignmentRunStatus.Disabled, off.Status);

        var dry = await PhoneJob(env, new StalePhoneAssignmentOptions { Enabled = true, DelayBetweenTicketsMs = 0 }).RunAsync(default);   // DryRun defaults to true
        Assert.Equal(StalePhoneAssignmentRunStatus.Completed, dry.Status);
        Assert.True(dry.DryRun);
        Assert.Equal(1, dry.WouldAssign);
        Assert.Equal(0, dry.Assigned);
        Assert.Empty(env.Assignments.Added);
        Assert.Empty(env.Audit.Entries);
        Assert.Equal(0, env.UnitOfWork.SaveChangesCallCount);

        var disabledThreshold = await PhoneJob(env, new StalePhoneAssignmentOptions { Enabled = true, StaleAfterHours = null }).RunAsync(default);
        Assert.Equal(StalePhoneAssignmentRunStatus.Disabled, disabledThreshold.Status);

        var busy = await PhoneJob(env, new StalePhoneAssignmentOptions { Enabled = true }, lockBusy: true).RunAsync(default);
        Assert.Equal(StalePhoneAssignmentRunStatus.Busy, busy.Status);
    }

    [Fact]
    public async Task StalePhoneJob_Live_AssignsOnlyStalePhoneTickets_ThrottledAndLeavingEverythingElseAlone()
    {
        var env = new Env(3);
        var stale1 = await env.SeedAsync(env.Facilities, liveAi: true, phone: true, lastActivity: Now.AddHours(-10));
        var stale2 = await env.SeedAsync(env.Leasing, TicketStatus.InProgress, liveAi: true, phone: true, lastActivity: Now.AddHours(-20));
        var stale3 = await env.SeedAsync(env.Facilities, liveAi: true, phone: true, lastActivity: Now.AddHours(-30));
        var recent = await env.SeedAsync(env.Facilities, liveAi: true, phone: true, lastActivity: Now.AddHours(-1));
        var chat = await env.SeedAsync(env.Facilities, liveAi: true, phone: false, lastActivity: Now.AddDays(-9));
        var plainUnowned = await env.SeedAsync(env.Facilities);                                  // ordinary unowned ticket: the backfill / reconciler's job, NOT this process
        var aiEnded = await env.SeedAsync(env.Facilities, aiEnded: true);                        // ended AI ticket: likewise
        var existing = env.Agents[2];
        var owned = await env.SeedAsync(env.Facilities, liveAi: true, phone: true, owner: existing, lastActivity: Now.AddDays(-3));

        var settings = new StalePhoneAssignmentOptions { Enabled = true, DryRun = false, DelayBetweenTicketsMs = 0, MaxAssignmentsPerRun = 2 };
        var first = await PhoneJob(env, settings).RunAsync(default);

        Assert.Equal(StalePhoneAssignmentRunStatus.Completed, first.Status);
        Assert.Equal(2, first.Assigned);                                                         // throttled by MaxAssignmentsPerRun
        Assert.True(first.LimitReached);
        Assert.NotNull(stale1.CurrentOwnerEmployeeId);
        Assert.NotNull(stale2.CurrentOwnerEmployeeId);
        Assert.Null(stale3.CurrentOwnerEmployeeId);

        var second = await PhoneJob(env, settings).RunAsync(default);                            // the next run finishes the backlog; assigned tickets are no longer candidates
        Assert.Equal(1, second.Assigned);
        Assert.NotNull(stale3.CurrentOwnerEmployeeId);

        var third = await PhoneJob(env, settings).RunAsync(default);
        Assert.Equal(0, third.Assigned);                                                         // idempotent

        Assert.Null(recent.CurrentOwnerEmployeeId);
        Assert.Null(chat.CurrentOwnerEmployeeId);
        Assert.Null(plainUnowned.CurrentOwnerEmployeeId);
        Assert.Null(aiEnded.CurrentOwnerEmployeeId);
        Assert.Equal(existing, owned.CurrentOwnerEmployeeId);
        Assert.Equal(3, env.Assignments.Added.Count);
        Assert.All(env.Audit.Entries.Where(e => e.Action == "BackfillAssign"), e => Assert.Contains("Reason=StalePhoneAi", e.AfterValue!));
        Assert.Empty(env.Events.All);
    }

    [Fact]
    public async Task StalePhoneJob_Live_IsRefusedWithoutAnEligibleAgent_BeforeTouchingAnyTicket()
    {
        var env = new Env(0);
        var ticket = await env.SeedAsync(env.Facilities, liveAi: true, phone: true, lastActivity: Now.AddHours(-10));

        var result = await PhoneJob(env, new StalePhoneAssignmentOptions { Enabled = true, DryRun = false }).RunAsync(default);

        Assert.Equal(StalePhoneAssignmentRunStatus.NoEligiblePool, result.Status);
        Assert.Null(ticket.CurrentOwnerEmployeeId);
        Assert.Equal(0, env.UnitOfWork.SaveChangesCallCount);
    }


    /// <summary>Evidence whose ticket gains fresh customer activity after it has been read once (the race between listing and writing).</summary>
    private sealed class ActivityArrivesAfterFirstRead(
        CallCenterUnassignedWorkReconcilerTests.StubEvidence inner, long ticketId, DateTime activityAtUtc) : ITicketUnassignedWorkRepository
    {
        private int _reads;

        public Task<IReadOnlyList<UnassignedTicketEvidence>> ListOpenUnownedAsync(long afterTicketId, int limit, CancellationToken cancellationToken = default) =>
            inner.ListOpenUnownedAsync(afterTicketId, limit, cancellationToken);

        public Task<IReadOnlyList<UnassignedTicketEvidence>> ListActiveUnownedAsync(long afterTicketId, int limit, CancellationToken cancellationToken = default) =>
            inner.ListActiveUnownedAsync(afterTicketId, limit, cancellationToken);

        public async Task<UnassignedTicketEvidence?> GetEvidenceAsync(long id, CancellationToken cancellationToken = default)
        {
            var row = await inner.GetEvidenceAsync(id, cancellationToken);
            return row is not null && id == ticketId && ++_reads > 1 ? row with { LastActivityUtc = activityAtUtc } : row;
        }
    }
}
