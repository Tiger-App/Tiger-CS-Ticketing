using TigerCS.Application.Modules.SlaAndEscalation.Abstractions;
using TigerCS.Application.Modules.SlaAndEscalation.Dto;
using TigerCS.Application.Modules.SlaAndEscalation.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.SlaAndEscalation.Fakes;
using TigerCS.Tests.Ticketing.Fakes;

namespace TigerCS.Tests.SlaAndEscalation.Services;

/// <summary>
/// The priority-downgrade approval workflow (ISSUE-023 Option B,
/// MVP-API-Contracts.md section 5.6): a downgrade never takes effect on
/// request, only a Department Head of the ticket's current department (or
/// above) can approve it, approval is one atomic unit, and a recorded breach
/// is never removed.
/// </summary>
public class PriorityDowngradeAppServiceTests
{
    private const int TicketDepartmentId = 2;
    private const int OtherDepartmentId = 3;
    private static readonly DateTime CreatedAt = new(2026, 10, 5, 6, 0, 0, DateTimeKind.Utc); // a Monday, inside business hours

    private sealed class MutableClock(DateTime utcNow) : TimeProvider
    {
        public DateTime UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
    }

    private sealed class FakePriorityDowngradeRequestRepository(FakeTicketRepository tickets) : IPriorityDowngradeRequestRepository
    {
        private readonly List<PriorityDowngradeRequest> _requests = [];
        private long _nextId = 1;

        public IReadOnlyList<PriorityDowngradeRequest> All => _requests;

        public Task<PriorityDowngradeRequest?> GetByIdAsync(long requestId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_requests.FirstOrDefault(r => r.PriorityDowngradeRequestId == requestId));

        public Task<PriorityDowngradeRequest?> GetPendingForTicketAsync(long ticketId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_requests.FirstOrDefault(r => r.TicketId == ticketId && r.Status == PriorityDowngradeRequestStatus.Pending));

        public Task<IReadOnlyList<PriorityDowngradeRequest>> ListByTicketIdAsync(long ticketId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PriorityDowngradeRequest>>([.. _requests.Where(r => r.TicketId == ticketId)]);

        public async Task<(IReadOnlyList<PendingDowngradeRow> Items, int TotalCount)> ListPendingAsync(
            IReadOnlyCollection<int>? departmentIds, DateTime nowUtc, int skip, int take, CancellationToken cancellationToken = default)
        {
            var rows = new List<PendingDowngradeRow>();
            foreach (var r in _requests.Where(r => r.Status == PriorityDowngradeRequestStatus.Pending && r.ExpiresAtUtc > nowUtc))
            {
                var ticket = (await tickets.GetByIdAsync(r.TicketId, cancellationToken))!;
                if (departmentIds is null || departmentIds.Contains(ticket.CurrentDepartmentId))
                {
                    rows.Add(new PendingDowngradeRow(r, ticket.TicketNumber, ticket.CurrentDepartmentId));
                }
            }

            return ([.. rows.Skip(skip).Take(take)], rows.Count);
        }

        public Task AddAsync(PriorityDowngradeRequest request, CancellationToken cancellationToken = default)
        {
            typeof(PriorityDowngradeRequest).GetProperty(nameof(PriorityDowngradeRequest.PriorityDowngradeRequestId))!
                .SetValue(request, _nextId++);
            _requests.Add(request);
            return Task.CompletedTask;
        }

        public void SetRowVersion(PriorityDowngradeRequest request, byte[] rowVersion) { }
    }

    private sealed class Harness
    {
        public required SlaServiceFixture Sla { get; init; }
        public required FakePriorityDowngradeRequestRepository Requests { get; init; }
        public required PriorityDowngradeAppService Service { get; init; }
        public required Ticket Ticket { get; init; }
        public required MutableClock Clock { get; init; }
        public required Guid OwnerId { get; init; }
        public Guid DepartmentHeadId { get; } = Guid.NewGuid();
        public Guid OtherDepartmentHeadId { get; } = Guid.NewGuid();

        public Task<DowngradeResult<PriorityDowngradeRequestResponseDto>> RequestAsync(
            byte newPriority = (byte)PriorityLevel.Medium, string reason = "Customer confirmed low urgency", Guid? caller = null) =>
            Service.RequestAsync(caller ?? OwnerId, [Roles.CsAgent], Ticket.TicketId, new CreateDowngradeRequestRequestDto(newPriority, reason));

        public Task<DowngradeResult<DowngradeDecisionResponseDto>> ApproveAsync(long requestId, Guid? caller = null, params string[] roles) =>
            Service.ApproveAsync(caller ?? DepartmentHeadId, roles.Length == 0 ? [Roles.DepartmentHead] : roles, requestId, null);
    }

    private static async Task<Harness> CreateAsync(byte priority = (byte)PriorityLevel.High)
    {
        var clock = new MutableClock(CreatedAt.AddMinutes(30));
        var sla = new SlaServiceFixture(timeProvider: clock);

        var ticket = Ticket.CreateVerified(
            "TG-CS-20261005-0001", TicketDepartmentId, unitReferenceId: 10, contactReferenceId: 20,
            categoryId: 5, priority, "AC not cooling", CreatedAt);
        await sla.Tickets.AddAsync(ticket);

        var ownerId = Guid.NewGuid();
        ticket.AssignTo(ownerId);
        ticket.ChangeStatus(TicketStatus.InProgress);

        await sla.DueDates.OpenInitialPeriodAsync(ticket, CreatedAt, ownerId, Guid.NewGuid());

        var requests = new FakePriorityDowngradeRequestRepository(sla.Tickets);
        var service = new PriorityDowngradeAppService(
            sla.Tickets, new FakePriorityRepository(), requests, sla.DepartmentAssignments, sla.DueDates,
            sla.UnitOfWork, sla.Audit, new PriorityDowngradeOptions(), clock, handlerRepository: FakeTicketInteractionHandlerRepository.Permissive());

        var harness = new Harness { Sla = sla, Requests = requests, Service = service, Ticket = ticket, Clock = clock, OwnerId = ownerId };
        sla.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(harness.DepartmentHeadId, TicketDepartmentId, isPrimary: true, CreatedAt, assignedByEmployeeId: null));
        sla.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(harness.OtherDepartmentHeadId, OtherDepartmentId, isPrimary: true, CreatedAt, assignedByEmployeeId: null));
        return harness;
    }

    private static async Task<PriorityDowngradeRequestResponseDto> RequestPendingAsync(Harness h, byte newPriority = (byte)PriorityLevel.Medium)
    {
        var result = await h.RequestAsync(newPriority);
        Assert.Equal(DowngradeOutcome.Success, result.Outcome);
        return result.Response!;
    }

    // -----------------------------------------------------------------
    // Request
    // -----------------------------------------------------------------

    [Fact]
    public async Task Request_CreatesPendingRequest_RecordsRequester_AndLeavesPriorityAndSlaUntouched()
    {
        var h = await CreateAsync();
        var periodBefore = await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId);
        var resolutionDueBefore = periodBefore!.ResolutionDueAtUtc;

        var dto = await RequestPendingAsync(h);

        Assert.Equal("Pending", dto.Status);
        Assert.Equal(h.OwnerId, dto.RequestedByEmployeeId);
        Assert.Equal((byte)PriorityLevel.High, dto.CurrentPriorityId);
        Assert.Equal((byte)PriorityLevel.Medium, dto.RequestedPriorityId);
        Assert.Equal(h.Clock.UtcNow.AddDays(7), dto.ExpiresAtUtc);
        Assert.Null(dto.DecidedByEmployeeId);

        // While pending: nothing about the ticket or its SLA has moved.
        Assert.Equal((byte)PriorityLevel.High, h.Ticket.PriorityId);
        var period = Assert.Single(h.Sla.SlaInstances.All);
        Assert.Same(periodBefore, period);
        Assert.Null(period.PeriodEndAtUtc);
        Assert.Equal(SlaChangeReason.InitialCreation, period.ChangeReason);
        Assert.Equal((byte)PriorityLevel.High, period.PriorityId);
        Assert.Equal(resolutionDueBefore, period.ResolutionDueAtUtc);
    }

    [Fact]
    public async Task Request_WritesARequestPriorityDowngradeAuditRow_WithActorAndBeforeAfter()
    {
        var h = await CreateAsync();

        await RequestPendingAsync(h);

        var audit = Assert.Single(h.Sla.Audit.Entries, e => e.Action == "RequestPriorityDowngrade");
        Assert.Equal(h.OwnerId, audit.ActorEmployeeId);
        Assert.Contains("PriorityId=2", audit.BeforeValue);
        Assert.Contains("Status=Pending", audit.AfterValue);
        Assert.Contains("RequestedPriorityId=3", audit.AfterValue);
    }

    [Fact]
    public async Task Request_WhileOneIsPending_IsAConflict_NamingTheExistingRequest()
    {
        var h = await CreateAsync();
        var first = await RequestPendingAsync(h);

        var second = await h.RequestAsync((byte)PriorityLevel.Low);

        Assert.Equal(DowngradeOutcome.AlreadyPending, second.Outcome);
        Assert.Equal(first.PriorityDowngradeRequestId, second.Existing!.PriorityDowngradeRequestId);
        Assert.Single(h.Requests.All);
    }

    [Theory]
    [InlineData((byte)PriorityLevel.High)]     // same
    [InlineData((byte)PriorityLevel.Critical)] // an upgrade
    public async Task Request_ThatIsNotADecrease_IsRefused(byte newPriority)
    {
        var h = await CreateAsync();

        var result = await h.RequestAsync(newPriority);

        Assert.Equal(DowngradeOutcome.NotADowngrade, result.Outcome);
        Assert.Empty(h.Requests.All);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Request_WithoutAReason_IsRefused(string reason)
    {
        var h = await CreateAsync();

        var result = await h.RequestAsync(reason: reason);

        Assert.Equal(DowngradeOutcome.InvalidRequest, result.Outcome);
        Assert.Empty(h.Requests.All);
    }

    [Fact]
    public async Task Request_ForAnUnknownPriority_IsRefused()
    {
        var h = await CreateAsync();

        Assert.Equal(DowngradeOutcome.InvalidRequest, (await h.RequestAsync(newPriority: 9)).Outcome);
    }

    [Fact]
    public async Task Request_OnAnUnclassifiedTicket_IsRefused_BecauseTheFirstPriorityIsNotADowngrade()
    {
        var h = await CreateAsync();
        var unclassified = Ticket.CreateUnclassified("TG-CS-20261005-0002", TicketDepartmentId, "Inquiry", CreatedAt);
        unclassified.AssignTo(h.OwnerId);
        await h.Sla.Tickets.AddAsync(unclassified);

        var result = await h.Service.RequestAsync(
            h.OwnerId, [Roles.CsAgent], unclassified.TicketId, new CreateDowngradeRequestRequestDto((byte)PriorityLevel.Low, "reason"));

        Assert.Equal(DowngradeOutcome.TicketNotClassified, result.Outcome);
    }

    [Fact]
    public async Task Request_ByADepartmentEmployeeOutsideTheTicketsDepartment_IsForbidden()
    {
        var h = await CreateAsync();

        var result = await h.Service.RequestAsync(
            Guid.NewGuid(), [Roles.DepartmentEmployee], h.Ticket.TicketId,
            new CreateDowngradeRequestRequestDto((byte)PriorityLevel.Low, "reason"));

        Assert.Equal(DowngradeOutcome.Forbidden, result.Outcome);
    }

    [Fact]
    public async Task Request_AfterThePreviousOneExpired_ExpiresTheOldOne_AndCreatesANewPendingRequest()
    {
        var h = await CreateAsync();
        var first = await RequestPendingAsync(h);
        h.Clock.UtcNow = h.Clock.UtcNow.AddDays(8);

        var second = await h.RequestAsync((byte)PriorityLevel.Low);

        Assert.Equal(DowngradeOutcome.Success, second.Outcome);
        Assert.Equal(PriorityDowngradeRequestStatus.Expired,
            h.Requests.All.Single(r => r.PriorityDowngradeRequestId == first.PriorityDowngradeRequestId).Status);
        Assert.Equal(PriorityDowngradeRequestStatus.Pending, h.Requests.All.Single(r => r.RequestedPriorityId == (byte)PriorityLevel.Low).Status);
    }

    // -----------------------------------------------------------------
    // Approve
    // -----------------------------------------------------------------

    [Fact]
    public async Task Approve_ByTheTicketsDepartmentHead_ChangesPriority_AndReplacesTheSlaPeriod_Atomically()
    {
        var h = await CreateAsync();
        var request = await RequestPendingAsync(h);
        h.Clock.UtcNow = h.Clock.UtcNow.AddHours(2);
        var approvedAt = h.Clock.UtcNow;
        var committedBefore = h.Sla.UnitOfWork.TransactionsCommitted;
        var original = await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId);

        var result = await h.ApproveAsync(request.PriorityDowngradeRequestId);

        Assert.Equal(DowngradeOutcome.Success, result.Outcome);
        Assert.Equal("Approved", result.Response!.Request.Status);
        Assert.Equal(h.DepartmentHeadId, result.Response.Request.DecidedByEmployeeId);
        Assert.Equal((byte)PriorityLevel.Medium, h.Ticket.PriorityId);

        // One transaction, committed once.
        Assert.Equal(committedBefore + 1, h.Sla.UnitOfWork.TransactionsCommitted);

        var periods = await h.Sla.SlaInstances.ListByTicketIdAsync(h.Ticket.TicketId);
        Assert.Equal(2, periods.Count);
        Assert.Equal(approvedAt, periods[0].PeriodEndAtUtc);
        Assert.Same(original, periods[0]);

        var current = periods[1];
        Assert.Null(current.PeriodEndAtUtc);
        Assert.Equal(SlaChangeReason.Downgrade, current.ChangeReason);
        Assert.Equal(h.DepartmentHeadId, current.ApprovedByEmployeeId);
        Assert.Equal((byte)PriorityLevel.Medium, current.PriorityId);
        Assert.Equal(approvedAt, current.PeriodStartAtUtc);

        var (_, expectedResolutionDue) = await h.Sla.DueDates.ComputeDueDatesAsync((byte)PriorityLevel.Medium, approvedAt);
        Assert.Equal(expectedResolutionDue, current.ResolutionDueAtUtc);
        Assert.True(current.ResolutionDueAtUtc >= original!.ResolutionDueAtUtc, "a downgrade never tightens the deadline");

        // First Response is carried, never restarted.
        Assert.Equal(original.FirstResponseDueAtUtc, current.FirstResponseDueAtUtc);
        Assert.Equal(request.PriorityDowngradeRequestId, result.Response.Request.PriorityDowngradeRequestId);
        Assert.Equal(current.PeriodStartAtUtc, result.Response.NewSlaPeriod!.PeriodStartAtUtc);
    }

    [Fact]
    public async Task Approve_PreservesBreachFlags_NeverRemovingOrReversingThem()
    {
        var h = await CreateAsync();
        var original = (await h.Sla.SlaInstances.GetCurrentAsync(h.Ticket.TicketId))!;
        original.MarkBreached(SlaDeadlineType.FirstResponse);
        original.MarkBreached(SlaDeadlineType.Resolution);
        var request = await RequestPendingAsync(h);
        h.Clock.UtcNow = original.ResolutionDueAtUtc.AddHours(1);
        var approvedAt = h.Clock.UtcNow;

        var result = await h.ApproveAsync(request.PriorityDowngradeRequestId);

        Assert.Equal(DowngradeOutcome.Success, result.Outcome);
        var periods = await h.Sla.SlaInstances.ListByTicketIdAsync(h.Ticket.TicketId);

        // The breach stays recorded on the original period, permanently.
        Assert.True(periods[0].FirstResponseBreached);
        Assert.True(periods[0].ResolutionBreached);

        // First Response result is carried onto the new period; the Resolution
        // clock starts fresh from the approval moment with a future deadline,
        // so the sweep neither re-flags nor loses the earlier breach.
        Assert.True(periods[1].FirstResponseBreached);
        Assert.False(periods[1].ResolutionBreached);
        Assert.True(periods[1].ResolutionDueAtUtc > approvedAt);
        Assert.Equal(original.FirstResponseDueAtUtc, periods[1].FirstResponseDueAtUtc);
    }

    [Fact]
    public async Task Approve_WritesAuditRows_WithTheApproverAsActor_AndBeforeAfterValues()
    {
        var h = await CreateAsync();
        var request = await RequestPendingAsync(h);

        await h.ApproveAsync(request.PriorityDowngradeRequestId);

        var audit = Assert.Single(h.Sla.Audit.Entries, e => e.Action == "ApprovePriorityDowngrade");
        Assert.Equal(h.DepartmentHeadId, audit.ActorEmployeeId);
        Assert.NotEqual(h.OwnerId, audit.ActorEmployeeId);
        Assert.Contains("Status=Pending", audit.BeforeValue);
        Assert.Contains("PriorityId=2", audit.BeforeValue);
        Assert.Contains("Status=Approved", audit.AfterValue);
        Assert.Contains("PriorityId=3", audit.AfterValue);

        // The SLA recomputation is itself audited with the ended period's breach state.
        Assert.Contains(h.Sla.Audit.Entries, e =>
            e.Action == "ComputeSlaDueDates" && e.ActorEmployeeId == h.DepartmentHeadId && e.BeforeValue!.Contains("Downgrade"));
    }

    [Fact]
    public async Task Approve_ByANonDepartmentHead_IsForbidden_AndChangesNothing()
    {
        var h = await CreateAsync();
        var request = await RequestPendingAsync(h);

        var result = await h.ApproveAsync(request.PriorityDowngradeRequestId, Guid.NewGuid(), Roles.CsAgent);

        Assert.Equal(DowngradeOutcome.Forbidden, result.Outcome);
        AssertNothingChanged(h);
    }

    [Fact]
    public async Task Approve_ByADepartmentEmployeeOfTheSameDepartment_IsForbidden()
    {
        var h = await CreateAsync();
        var request = await RequestPendingAsync(h);

        var result = await h.ApproveAsync(request.PriorityDowngradeRequestId, h.DepartmentHeadId, Roles.DepartmentEmployee);

        Assert.Equal(DowngradeOutcome.Forbidden, result.Outcome);
        AssertNothingChanged(h);
    }

    [Fact]
    public async Task Approve_ByTheDepartmentHeadOfAnotherDepartment_IsForbidden()
    {
        var h = await CreateAsync();
        var request = await RequestPendingAsync(h);

        var result = await h.ApproveAsync(request.PriorityDowngradeRequestId, h.OtherDepartmentHeadId);

        Assert.Equal(DowngradeOutcome.Forbidden, result.Outcome);
        AssertNothingChanged(h);
    }

    [Theory]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.SystemAdministrator)]
    public async Task Approve_ByTheAboveTierOrTheAdministratorOverride_IsAllowedWithoutDepartmentMembership(string role)
    {
        var h = await CreateAsync();
        var request = await RequestPendingAsync(h);

        var result = await h.ApproveAsync(request.PriorityDowngradeRequestId, Guid.NewGuid(), role);

        Assert.Equal(DowngradeOutcome.Success, result.Outcome);
        Assert.Equal((byte)PriorityLevel.Medium, h.Ticket.PriorityId);
    }

    [Fact]
    public async Task Approve_ByTheRequester_IsForbidden_EvenForADepartmentHead()
    {
        var h = await CreateAsync();
        // The requester is themselves the Department Head.
        var request = await h.Service.RequestAsync(
            h.DepartmentHeadId, [Roles.DepartmentHead], h.Ticket.TicketId,
            new CreateDowngradeRequestRequestDto((byte)PriorityLevel.Medium, "I think it is low"));

        var result = await h.ApproveAsync(request.Response!.PriorityDowngradeRequestId, h.DepartmentHeadId);

        Assert.Equal(DowngradeOutcome.SelfApprovalForbidden, result.Outcome);
        AssertNothingChanged(h);
    }

    [Fact]
    public async Task Approve_AfterExpiry_IsGone_PersistsTheExpiry_AndChangesNothingElse()
    {
        var h = await CreateAsync();
        var request = await RequestPendingAsync(h);
        h.Clock.UtcNow = h.Clock.UtcNow.AddDays(8);

        var result = await h.ApproveAsync(request.PriorityDowngradeRequestId);

        Assert.Equal(DowngradeOutcome.Expired, result.Outcome);
        Assert.Equal(PriorityDowngradeRequestStatus.Expired, h.Requests.All.Single().Status);
        Assert.Equal((byte)PriorityLevel.High, h.Ticket.PriorityId);
        Assert.Single(h.Sla.SlaInstances.All);
        Assert.Contains(h.Sla.Audit.Entries, e => e.Action == "ExpirePriorityDowngrade");
    }

    [Fact]
    public async Task Approve_WhenThePriorityChangedSinceTheRequest_IsAStaleConflict()
    {
        var h = await CreateAsync();
        var request = await RequestPendingAsync(h);
        // Simulate another path having moved the priority (an approved upgrade, say).
        h.Ticket.ChangePriority((byte)PriorityLevel.Critical);

        var result = await h.ApproveAsync(request.PriorityDowngradeRequestId);

        Assert.Equal(DowngradeOutcome.StalePriority, result.Outcome);
        Assert.Equal((byte)PriorityLevel.Critical, h.Ticket.PriorityId);
        Assert.Equal(PriorityDowngradeRequestStatus.Pending, h.Requests.All.Single().Status);
        Assert.Single(h.Sla.SlaInstances.All);
    }

    [Fact]
    public async Task Approve_ARequestThatWasAlreadyDecided_IsNotPending()
    {
        var h = await CreateAsync();
        var request = await RequestPendingAsync(h);
        await h.ApproveAsync(request.PriorityDowngradeRequestId);

        var again = await h.ApproveAsync(request.PriorityDowngradeRequestId);

        Assert.Equal(DowngradeOutcome.NotPending, again.Outcome);
        Assert.Equal(2, h.Sla.SlaInstances.All.Count); // no third period
    }

    [Fact]
    public async Task Approve_ForAnUnknownRequest_IsNotFound()
    {
        var h = await CreateAsync();

        Assert.Equal(DowngradeOutcome.NotFound, (await h.ApproveAsync(999)).Outcome);
    }

    [Fact]
    public async Task Approve_WhenTheSaveFails_NothingIsCommitted()
    {
        var h = await CreateAsync();
        var request = await RequestPendingAsync(h);
        var committedBefore = h.Sla.UnitOfWork.TransactionsCommitted;
        var rolledBackBefore = h.Sla.UnitOfWork.TransactionsRolledBack;
        h.Sla.UnitOfWork.ThrowTicketConcurrencyConflictOnCall = h.Sla.UnitOfWork.SaveChangesCallCount + 1;

        var result = await h.ApproveAsync(request.PriorityDowngradeRequestId);

        Assert.Equal(DowngradeOutcome.ConcurrencyConflict, result.Outcome);
        Assert.Equal(committedBefore, h.Sla.UnitOfWork.TransactionsCommitted);
        Assert.Equal(rolledBackBefore + 1, h.Sla.UnitOfWork.TransactionsRolledBack);
    }

    [Fact]
    public async Task Approve_WhenTheNewPrioritysPolicyCannotBeLoaded_FailsBeforeChangingAnything()
    {
        var h = await CreateAsync();
        var request = await RequestPendingAsync(h);
        h.Sla.Policies.Clear();
        var committedBefore = h.Sla.UnitOfWork.TransactionsCommitted;

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.ApproveAsync(request.PriorityDowngradeRequestId));

        // Mid-way failure leaves no partial effect: priority, request and the
        // current period are exactly as before, and nothing was committed.
        AssertNothingChanged(h);
        Assert.Equal(committedBefore, h.Sla.UnitOfWork.TransactionsCommitted);
        Assert.DoesNotContain(h.Sla.Audit.Entries, e => e.Action == "ApprovePriorityDowngrade");
    }

    [Fact]
    public async Task Approve_OnATicketWithNoSlaPeriodYet_ChangesThePriorityAlone()
    {
        var h = await CreateAsync();
        var provisional = Ticket.CreateUnverified("TG-CS-20261005-0003", TicketDepartmentId, 5, (byte)PriorityLevel.High, "x", CreatedAt);
        provisional.AssignTo(h.OwnerId);
        await h.Sla.Tickets.AddAsync(provisional);
        var request = await h.Service.RequestAsync(
            h.OwnerId, [Roles.CsAgent], provisional.TicketId, new CreateDowngradeRequestRequestDto((byte)PriorityLevel.Low, "reason"));

        var result = await h.ApproveAsync(request.Response!.PriorityDowngradeRequestId);

        Assert.Equal(DowngradeOutcome.Success, result.Outcome);
        Assert.Equal((byte)PriorityLevel.Low, provisional.PriorityId);
        Assert.Null(result.Response!.NewSlaPeriod);
        Assert.DoesNotContain(h.Sla.SlaInstances.All, i => i.TicketId == provisional.TicketId);
    }

    // -----------------------------------------------------------------
    // Reject
    // -----------------------------------------------------------------

    [Fact]
    public async Task Reject_LeavesThePriorityUnchanged_RecordsTheDecider_AndAudits()
    {
        var h = await CreateAsync();
        var request = await RequestPendingAsync(h);

        var result = await h.Service.RejectAsync(
            h.DepartmentHeadId, [Roles.DepartmentHead], request.PriorityDowngradeRequestId,
            new RejectDowngradeRequestRequestDto("Still impacting tenants."));

        Assert.Equal(DowngradeOutcome.Success, result.Outcome);
        Assert.Equal("Rejected", result.Response!.Status);
        Assert.Equal(h.DepartmentHeadId, result.Response.DecidedByEmployeeId);
        Assert.Equal("Still impacting tenants.", result.Response.DecisionNote);
        Assert.Equal((byte)PriorityLevel.High, h.Ticket.PriorityId);
        Assert.Single(h.Sla.SlaInstances.All);
        Assert.Null(h.Sla.SlaInstances.All[0].PeriodEndAtUtc);

        var audit = Assert.Single(h.Sla.Audit.Entries, e => e.Action == "RejectPriorityDowngrade");
        Assert.Equal(h.DepartmentHeadId, audit.ActorEmployeeId);
        Assert.Contains("Status=Rejected", audit.AfterValue);

        // A decided request cannot then be approved, and a new one may be raised.
        Assert.Equal(DowngradeOutcome.NotPending, (await h.ApproveAsync(request.PriorityDowngradeRequestId)).Outcome);
        Assert.Equal(DowngradeOutcome.Success, (await h.RequestAsync()).Outcome);
    }

    [Fact]
    public async Task Reject_WithoutANote_IsRefused()
    {
        var h = await CreateAsync();
        var request = await RequestPendingAsync(h);

        var result = await h.Service.RejectAsync(
            h.DepartmentHeadId, [Roles.DepartmentHead], request.PriorityDowngradeRequestId, new RejectDowngradeRequestRequestDto(" "));

        Assert.Equal(DowngradeOutcome.InvalidRequest, result.Outcome);
        Assert.Equal(PriorityDowngradeRequestStatus.Pending, h.Requests.All.Single().Status);
    }

    [Fact]
    public async Task Reject_ByAnotherDepartmentsHead_IsForbidden()
    {
        var h = await CreateAsync();
        var request = await RequestPendingAsync(h);

        var result = await h.Service.RejectAsync(
            h.OtherDepartmentHeadId, [Roles.DepartmentHead], request.PriorityDowngradeRequestId,
            new RejectDowngradeRequestRequestDto("no"));

        Assert.Equal(DowngradeOutcome.Forbidden, result.Outcome);
        Assert.Equal(PriorityDowngradeRequestStatus.Pending, h.Requests.All.Single().Status);
    }

    [Fact]
    public async Task Reject_AfterExpiry_IsGone()
    {
        var h = await CreateAsync();
        var request = await RequestPendingAsync(h);
        h.Clock.UtcNow = h.Clock.UtcNow.AddDays(8);

        var result = await h.Service.RejectAsync(
            h.DepartmentHeadId, [Roles.DepartmentHead], request.PriorityDowngradeRequestId, new RejectDowngradeRequestRequestDto("late"));

        Assert.Equal(DowngradeOutcome.Expired, result.Outcome);
    }

    // -----------------------------------------------------------------
    // Reads
    // -----------------------------------------------------------------

    [Fact]
    public async Task Pending_ForADepartmentHead_ListsOnlyTheirOwnDepartmentsRequests()
    {
        var h = await CreateAsync();
        await RequestPendingAsync(h);

        var own = await h.Service.ListPendingAsync(h.DepartmentHeadId, [Roles.DepartmentHead], null, 1, 25);
        var other = await h.Service.ListPendingAsync(h.OtherDepartmentHeadId, [Roles.DepartmentHead], null, 1, 25);
        var wrongFilter = await h.Service.ListPendingAsync(h.OtherDepartmentHeadId, [Roles.DepartmentHead], TicketDepartmentId, 1, 25);
        var manager = await h.Service.ListPendingAsync(Guid.NewGuid(), [Roles.CsManager], null, 1, 25);

        Assert.Single(own.Response!.Items);
        Assert.Equal("TG-CS-20261005-0001", own.Response.Items[0].TicketNumber);
        Assert.Empty(other.Response!.Items);
        Assert.Equal(DowngradeOutcome.Forbidden, wrongFilter.Outcome);
        Assert.Single(manager.Response!.Items);
    }

    [Fact]
    public async Task Pending_ForAnAgent_IsForbidden()
    {
        var h = await CreateAsync();

        Assert.Equal(DowngradeOutcome.Forbidden, (await h.Service.ListPendingAsync(h.OwnerId, [Roles.CsAgent], null, 1, 25)).Outcome);
    }

    [Fact]
    public async Task History_ReportsAnExpiredPendingRequestAsExpired()
    {
        var h = await CreateAsync();
        await RequestPendingAsync(h);
        h.Clock.UtcNow = h.Clock.UtcNow.AddDays(8);

        var history = await h.Service.ListForTicketAsync(h.OwnerId, [Roles.CsAgent], h.Ticket.TicketId);

        Assert.Equal("Expired", Assert.Single(history.Response!).Status);
    }

    // -----------------------------------------------------------------
    // The bare-decrease guard
    // -----------------------------------------------------------------

    [Fact]
    public async Task Ticket_ABareDecrease_IsRefusedWithoutAnApprovedDowngrade()
    {
        var h = await CreateAsync();

        Assert.Throws<PriorityDowngradeRequiresApprovalException>(() => h.Ticket.ChangePriority((byte)PriorityLevel.Low));
        Assert.Equal((byte)PriorityLevel.High, h.Ticket.PriorityId);

        h.Ticket.ChangePriority((byte)PriorityLevel.Low, downgradeApproved: true);
        Assert.Equal((byte)PriorityLevel.Low, h.Ticket.PriorityId);
    }

    [Fact]
    public void Ticket_TheFirstPriorityOfAnUnclassifiedTicket_IsClassification_NotAChange()
    {
        var ticket = Ticket.CreateUnclassified("TG-CS-20261005-0009", TicketDepartmentId, "Inquiry", CreatedAt);

        ticket.Classify(categoryId: 5, (byte)PriorityLevel.Low);
        Assert.Equal((byte)PriorityLevel.Low, ticket.PriorityId);

        Assert.Throws<InvalidOperationException>(() =>
            Ticket.CreateUnclassified("TG-CS-20261005-0010", TicketDepartmentId, "Inquiry", CreatedAt).ChangePriority((byte)PriorityLevel.Low, true));
    }

    [Fact]
    public void Request_Entity_CannotBeDecidedByItsRequester_OrTwice()
    {
        var requester = Guid.NewGuid();
        var request = PriorityDowngradeRequest.Create(1, 2, 3, "reason", requester, CreatedAt, CreatedAt.AddDays(7));

        Assert.Throws<PriorityDowngradeSelfDecisionException>(() => request.Approve(requester, CreatedAt.AddHours(1)));

        request.Approve(Guid.NewGuid(), CreatedAt.AddHours(1));
        Assert.Throws<PriorityDowngradeRequestNotPendingException>(() => request.Reject(Guid.NewGuid(), CreatedAt.AddHours(2), "x"));
    }

    private static void AssertNothingChanged(Harness h)
    {
        Assert.Equal((byte)PriorityLevel.High, h.Ticket.PriorityId);
        Assert.Equal(PriorityDowngradeRequestStatus.Pending, h.Requests.All.Single().Status);
        var period = Assert.Single(h.Sla.SlaInstances.All);
        Assert.Null(period.PeriodEndAtUtc);
        Assert.Equal(SlaChangeReason.InitialCreation, period.ChangeReason);
    }
}
