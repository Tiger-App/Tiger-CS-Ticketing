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
            Guid? owner = null)
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
                RequiresHumanFollowUp: humanFollowUp, OpenHandoff: false, HumanEvidence: false, AnsweringEmployeeId: answeredBy,
                AnsweringGenesysUserId: null, PhonePickupByUnmappedAgent: false, UnmappedGenesysAgentId: null, LastActivityUtc: created ?? Old,
                LeftCallCenter: false, ReleasedByPerson: false);
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
            [CsAgentBackfillClass.AiEndedNoHumanNeed] = await env.SeedAsync(env.Facilities, aiEnded: true),
            [CsAgentBackfillClass.HumanHandledNeedsManualReview] = await env.SeedAsync(env.Facilities, answeredBy: Guid.NewGuid()),
            [CsAgentBackfillClass.TooNew] = await env.SeedAsync(env.Facilities, created: Now.AddMinutes(-1)),
        };

        foreach (var (expected, ticket) in tickets)
        {
            Assert.Equal(expected, CsAgentBackfillClassifier.Classify(env.Evidence.Rows[ticket.TicketId], Now, env.Options));
        }

        // A live AI conversation that DID ask for a person is human work; an AI-ended one is eligible only when the business opts in.
        var asked = await env.SeedAsync(env.Facilities, liveAi: true, humanFollowUp: true);
        Assert.Equal(CsAgentBackfillClass.Eligible, CsAgentBackfillClassifier.Classify(env.Evidence.Rows[asked.TicketId], Now, env.Options));
        env.Options.IncludeAiEndedWithoutHuman = true;
        Assert.Equal(CsAgentBackfillClass.Eligible,
            CsAgentBackfillClassifier.Classify(env.Evidence.Rows[tickets[CsAgentBackfillClass.AiEndedNoHumanNeed].TicketId], Now, env.Options));

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
            await env.SeedAsync(env.Facilities, answeredBy: Guid.NewGuid()),
            await env.SeedAsync(env.Facilities, aiEnded: true),
            await env.SeedAsync(env.Facilities, created: Now.AddSeconds(-30)),
        };

        foreach (var t in tickets)
        {
            Assert.Equal(CsAgentBackfillOutcome.Skipped, (await env.Service().TryBackfillAsync(t.TicketId, "b", env.Options, false)).Outcome);
            Assert.Null(t.CurrentOwnerEmployeeId);
        }

        Assert.Equal(0, env.UnitOfWork.SaveChangesCallCount);
        Assert.Empty(env.Assignments.Added);
        Assert.Empty(env.Rotation.Rows);   // the cursor was never even created
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
}
