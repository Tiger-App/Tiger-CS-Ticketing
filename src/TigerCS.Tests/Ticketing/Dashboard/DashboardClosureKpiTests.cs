using TigerCS.Application.Modules.Notifications;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Tests.Ticketing.Dashboard;

/// <summary>
/// The four closure KPIs (Reviewed Tickets, Not Reviewed, Closed by AI,
/// Closed by Agent), end to end from the application service down to real
/// SQL. Each is proven against the persisted facts the system actually
/// records — <c>TicketReviews</c> for feedback and the closing
/// <c>AuditEntries</c> row for who closed — and against the ticket queue's
/// <c>closureCategory</c> drill-down, which must list exactly what the card
/// counted.
/// </summary>
public sealed class DashboardClosureKpiTests : IDisposable
{
    private static readonly DateTime Now = DashboardSqliteFixture.Now;
    private static readonly string[] CrossDepartment = [Roles.CsSupervisor];

    private readonly DashboardSqliteFixture _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly CustomerNotificationModePolicy TestMode = new("true", "uat@example.test", null, null);
    private static readonly CustomerNotificationModePolicy ProductionMode = CustomerNotificationModePolicy.ProductionDefault();

    private Task<DashboardOverviewDto> OverviewAsync(
        Guid caller, string[] roles, DashboardOverviewRequestDto? request = null, CustomerNotificationModePolicy? mode = null)
    {
        using var context = _db.CreateContext();
        return _db.CreateService(context, mode: mode ?? ProductionMode).GetOverviewAsync(caller, roles, request ?? new DashboardOverviewRequestDto());
    }

    /// <summary>The ticket queue exactly as the card's link opens it: Closed status, the category, and the card's closure date range.</summary>
    private async Task<int> QueueCountAsync(
        Guid caller, string[] roles, TicketClosureCategory category, DateOnly? closedFrom = null, DateOnly? closedTo = null,
        int? departmentId = null, string? ticketStatus = nameof(TicketStatus.Closed), CustomerNotificationModePolicy? mode = null)
    {
        using var context = _db.CreateContext();
        var result = await _db.CreateQueryService(context, mode: mode ?? ProductionMode).GetQueueAsync(
            caller, roles,
            new TicketListRequestDto(
                departmentId, null, null, ticketStatus, null, null, null, null, null, 1, 100,
                ClosureCategory: category.ToString(), ClosedFrom: closedFrom, ClosedTo: closedTo));
        return result.TotalCount;
    }

    private Ticket ClosedTicket(int departmentId, string? closureAction, DateTime? createdAtUtc = null, DateTime? closedAtUtc = null)
    {
        using var context = _db.CreateContext();
        var ticket = _db.AddTicket(context, departmentId, createdAtUtc: createdAtUtc, status: TicketStatus.Closed, closedAtUtc: closedAtUtc);
        if (closureAction is not null)
        {
            _db.AddClosureAudit(context, ticket, closureAction, actor: Guid.NewGuid());
        }

        return ticket;
    }

    // ---------------------------------------------------------------
    // Reviewed Tickets / Not Reviewed
    // ---------------------------------------------------------------

    [Fact]
    public async Task ReviewedTickets_CountsClosedTicketsWithASubmittedReview_AndNotReviewedCountsTheRest()
    {
        var reviewed = ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        var alsoReviewed = ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAi);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        ClosedTicket(_db.CustomerServiceId, null);
        using (var write = _db.CreateContext())
        {
            _db.AddReview(write, reviewed);
            _db.AddReview(write, alsoReviewed);
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(2, overview.Kpis.ReviewedTickets);
        Assert.Equal(2, overview.Kpis.NotReviewedTickets);
    }

    [Fact]
    public async Task ReviewedTickets_IgnoresInvitationsWithoutASubmission_AndTestModeReviews()
    {
        var invitedOnly = ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        var testReview = ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        var realReview = ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        using (var write = _db.CreateContext())
        {
            _db.AddReview(write, invitedOnly, submit: false);
            _db.AddReview(write, testReview, isTest: true);
            _db.AddReview(write, realReview);
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        // An invitation is not feedback and a Test-mode review is not customer
        // feedback — only the one real submission counts.
        Assert.Equal(1, overview.Kpis.ReviewedTickets);
        Assert.Equal(2, overview.Kpis.NotReviewedTickets);
    }

    [Fact]
    public async Task ReviewedAndNotReviewed_CountEachTicketOnce_WhateverItsReviewsAndInvitations()
    {
        var ticket = ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        using (var write = _db.CreateContext())
        {
            // A real and a Test-mode review, plus an extra unsubmitted invitation, on one cycle.
            _db.AddReview(write, ticket);
            _db.AddReview(write, ticket, isTest: true);
            _db.AddReview(write, ticket, submit: false);
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(1, overview.Kpis.ReviewedTickets);
        Assert.Equal(0, overview.Kpis.NotReviewedTickets);
    }

    [Fact]
    public async Task ReviewedTickets_OnlyCountsActiveAndResolvedTicketsAsNeither()
    {
        using var context = _db.CreateContext();
        var open = _db.AddTicket(context, _db.CustomerServiceId);
        var resolved = _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.Resolved);
        _db.AddReview(context, open);
        _db.AddReview(context, resolved);

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        // The KPIs are about Closed tickets; a review on a ticket that is not closed does not move them.
        Assert.Equal(0, overview.Kpis.ReviewedTickets);
        Assert.Equal(0, overview.Kpis.NotReviewedTickets);
    }

    [Fact]
    public async Task ReviewedTickets_ARecycledTicketNeedsAReviewForItsCurrentClosure()
    {
        using var context = _db.CreateContext();
        var ticket = _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.Closed);
        _db.AddReview(context, ticket); // cycle 0

        // Reopen and close again: cycle 1, not yet reviewed.
        ticket.Reopen(ticket.CurrentDepartmentId);
        ticket.AssignTo(_db.LifecycleWorkerId);
        ticket.Resolve(ResolutionOutcome.Resolved, null);
        ticket.Close();
        context.SaveChanges();
        _db.AddClosedHistory(context, ticket, Now.AddMinutes(-10));

        var beforeReview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);
        Assert.Equal(1, ticket.ReopenCount);
        Assert.Equal(0, beforeReview.Kpis.ReviewedTickets);
        Assert.Equal(1, beforeReview.Kpis.NotReviewedTickets);

        _db.AddReview(context, ticket); // cycle 1
        var afterReview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);
        Assert.Equal(1, afterReview.Kpis.ReviewedTickets);
        Assert.Equal(0, afterReview.Kpis.NotReviewedTickets);
    }

    [Fact]
    public async Task InTestMode_ReviewedAndNotReviewed_EvaluateTestModeReviews_SoUatCanValidateThem()
    {
        var testReviewed = ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        var realReviewed = ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        using (var context = _db.CreateContext())
        {
            _db.AddReview(context, testReviewed, isTest: true);
            _db.AddReview(context, realReviewed, isTest: false);
        }

        var uat = await OverviewAsync(Guid.NewGuid(), CrossDepartment, mode: TestMode);
        var production = await OverviewAsync(Guid.NewGuid(), CrossDepartment, mode: ProductionMode);

        Assert.True(uat.Kpis.ReviewsAreTestMode);
        Assert.Equal((1, 2), (uat.Kpis.ReviewedTickets, uat.Kpis.NotReviewedTickets));     // only the test-mode review counts in UAT
        Assert.False(production.Kpis.ReviewsAreTestMode);
        Assert.Equal((1, 2), (production.Kpis.ReviewedTickets, production.Kpis.NotReviewedTickets)); // only the real review counts in production

        // The cards and their lists agree in each mode.
        Assert.Equal(1, await QueueCountAsync(Guid.NewGuid(), CrossDepartment, TicketClosureCategory.Reviewed, mode: TestMode));
        Assert.Equal(2, await QueueCountAsync(Guid.NewGuid(), CrossDepartment, TicketClosureCategory.NotReviewed, mode: TestMode));
    }

    [Fact]
    public async Task ProductionSatisfactionStatistics_StillIgnoreTestModeReviews_WhateverTheDashboardShows()
    {
        var ticket = ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        using var context = _db.CreateContext();
        _db.AddReview(context, ticket, isTest: true);

        var uat = await OverviewAsync(Guid.NewGuid(), CrossDepartment, mode: TestMode);
        var summary = await new TigerCS.Infrastructure.Modules.CustomerReviews.TicketReviewRepository(context).GetProductionSummaryAsync();

        Assert.Equal(1, uat.Kpis.ReviewedTickets);
        Assert.Equal(0, summary.ReviewCount); // the production figure never sees the test review
    }

    // ---------------------------------------------------------------
    // Closed by AI / Closed by Agent
    // ---------------------------------------------------------------

    [Fact]
    public async Task ClosedByAi_UsesTheRecordedAiClosure_NeverInactivityOrDocumentDelivery()
    {
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAi);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAi);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedForCustomerInactivity);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedForDocumentDelivery);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        ClosedTicket(_db.CustomerServiceId, null); // closed with no audit trail at all

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(2, overview.Kpis.ClosedByAiTickets);
        Assert.Equal(1, overview.Kpis.ClosedByAgentTickets);
    }

    [Fact]
    public async Task ClosedByAgent_CountsOnlyClosuresRecordedAsAPersonClosingTheTicket()
    {
        using var context = _db.CreateContext();
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAi);
        // An open ticket with a stale "Close" row from an earlier cycle is not a closed ticket.
        var reopened = _db.AddTicket(context, _db.CustomerServiceId);
        _db.AddClosureAudit(context, reopened, TicketClosureAuditActions.ClosedByAgent);

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(3, overview.Kpis.ClosedByAgentTickets);
        Assert.Equal(1, overview.Kpis.ClosedByAiTickets);
    }

    [Fact]
    public async Task ClosedByAiAndAgent_AReopenedTicketIsJudgedByItsLatestClosure_AndCountedOnce()
    {
        using var context = _db.CreateContext();
        var ticket = _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.Closed);
        // First closed by the AI, reopened, then closed by a person: two closing rows, one ticket.
        _db.AddClosureAudit(context, ticket, TicketClosureAuditActions.ClosedByAi);
        _db.AddClosureAudit(context, ticket, TicketClosureAuditActions.ClosedByAgent, actor: Guid.NewGuid());
        // A duplicate agent row (a retried request) must not double count either.
        _db.AddClosureAudit(context, ticket, TicketClosureAuditActions.ClosedByAgent, actor: Guid.NewGuid());

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(1, overview.Kpis.ClosedByAgentTickets);
        Assert.Equal(0, overview.Kpis.ClosedByAiTickets);
    }

    [Fact]
    public void ClosureAuditActions_AreTheValuesTheLifecycleAlreadyWrites()
    {
        // Pinned against TicketLifecycleAppService, which writes these literals
        // (see its tests): a rename there must be a deliberate decision here.
        Assert.Equal("Close", TicketClosureAuditActions.ClosedByAgent);
        Assert.Equal("AutoCloseByAi", TicketClosureAuditActions.ClosedByAi);
        Assert.Equal("AutoCloseCustomerInactivity", TicketClosureAuditActions.ClosedForCustomerInactivity);
        Assert.Equal("AutoCloseDocumentDelivery", TicketClosureAuditActions.ClosedForDocumentDelivery);
    }

    // ---------------------------------------------------------------
    // Filters
    // ---------------------------------------------------------------

    [Fact]
    public async Task ClosureKpis_FollowTheDateRange_ByLatestClosureDate_NotCreationDate()
    {
        // Created long ago, closed yesterday: in the default (last 30 days) range.
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent, createdAtUtc: Now.AddDays(-90), closedAtUtc: Now.AddDays(-1));
        // Created yesterday, closed 45 days ago is impossible; created 60 days ago, closed 45 days ago: out of the default range.
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent, createdAtUtc: Now.AddDays(-60), closedAtUtc: Now.AddDays(-45));
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAi, createdAtUtc: Now.AddDays(-60), closedAtUtc: Now.AddDays(-45));

        var defaultRange = await OverviewAsync(Guid.NewGuid(), CrossDepartment); // last 30 days
        var wide = await OverviewAsync(Guid.NewGuid(), CrossDepartment,
            new DashboardOverviewRequestDto(DateFrom: DateOnly.FromDateTime(Now.AddDays(-100)), DateTo: DateOnly.FromDateTime(Now)));
        var old = await OverviewAsync(Guid.NewGuid(), CrossDepartment,
            new DashboardOverviewRequestDto(DateFrom: DateOnly.FromDateTime(Now.AddDays(-50)), DateTo: DateOnly.FromDateTime(Now.AddDays(-40))));

        Assert.Equal((1, 0), (defaultRange.Kpis.ClosedByAgentTickets, defaultRange.Kpis.ClosedByAiTickets));
        Assert.Equal((2, 1), (wide.Kpis.ClosedByAgentTickets, wide.Kpis.ClosedByAiTickets));
        Assert.Equal((1, 1), (old.Kpis.ClosedByAgentTickets, old.Kpis.ClosedByAiTickets));
    }

    [Fact]
    public async Task ClosureDateRange_IsInclusiveOfBothUtcDays_AndExclusiveOfTheNext()
    {
        var day = Now.Date.AddDays(-5);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent, closedAtUtc: day);                          // 00:00:00 first day — in
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent, closedAtUtc: day.AddDays(1).AddTicks(-1));  // last instant — in
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent, closedAtUtc: day.AddDays(1));               // next day 00:00 — out
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent, closedAtUtc: day.AddTicks(-1));             // previous day — out

        var one = DateOnly.FromDateTime(day);
        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment, new DashboardOverviewRequestDto(DateFrom: one, DateTo: one));

        Assert.Equal(2, overview.Kpis.ClosedByAgentTickets);
        Assert.Equal(2, await QueueCountAsync(Guid.NewGuid(), CrossDepartment, TicketClosureCategory.ClosedByAgent, one, one));
    }

    [Fact]
    public async Task AReopenedTicketIsDatedByItsLatestClosure()
    {
        using var context = _db.CreateContext();
        var ticket = _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddDays(-90), status: TicketStatus.Closed, closedAtUtc: Now.AddDays(-80));
        _db.AddClosureAudit(context, ticket, TicketClosureAuditActions.ClosedByAgent);
        // Closed again 3 days ago (a second status-history row for the same ticket).
        _db.AddClosedHistory(context, ticket, Now.AddDays(-3));

        var recent = await OverviewAsync(Guid.NewGuid(), CrossDepartment); // last 30 days
        var firstClosureWindow = await OverviewAsync(Guid.NewGuid(), CrossDepartment,
            new DashboardOverviewRequestDto(DateFrom: DateOnly.FromDateTime(Now.AddDays(-85)), DateTo: DateOnly.FromDateTime(Now.AddDays(-75))));

        Assert.Equal(1, recent.Kpis.ClosedByAgentTickets);      // counted once, by the latest closure
        Assert.Equal(0, firstClosureWindow.Kpis.ClosedByAgentTickets); // no longer dated by the first closure
    }

    // ---------------------------------------------------------------
    // Status filter
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("Open")]
    [InlineData("InProgress")]
    [InlineData("Resolved")]
    [InlineData("Closed")]
    public async Task ClosureKpis_AlwaysRepresentClosedTickets_WhateverStatusTheDashboardFilters(string statusFilter)
    {
        var reviewed = ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAi);
        ClosedTicket(_db.CollectionsId, TicketClosureAuditActions.ClosedByAgent);
        using (var context = _db.CreateContext())
        {
            _db.AddReview(context, reviewed);
            _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.InProgress);
        }

        var overview = await OverviewAsync(
            Guid.NewGuid(), CrossDepartment, new DashboardOverviewRequestDto(DepartmentId: _db.CustomerServiceId, TicketStatus: statusFilter));

        // Department and date range still apply; the status filter does not.
        Assert.Equal((1, 1, 1, 1), (overview.Kpis.ReviewedTickets, overview.Kpis.NotReviewedTickets, overview.Kpis.ClosedByAiTickets, overview.Kpis.ClosedByAgentTickets));
    }

    [Fact]
    public async Task ClosureKpis_RespectTheDepartmentFilter_AndNeverWidenTheCallersScope()
    {
        ClosedTicket(_db.CollectionsId, TicketClosureAuditActions.ClosedByAgent);
        ClosedTicket(_db.CollectionsId, TicketClosureAuditActions.ClosedByAi);
        ClosedTicket(_db.RegistrationId, TicketClosureAuditActions.ClosedByAgent);
        ClosedTicket(_db.RegistrationId, TicketClosureAuditActions.ClosedByAgent);

        var supervisorAll = await OverviewAsync(Guid.NewGuid(), CrossDepartment);
        var supervisorCollections = await OverviewAsync(Guid.NewGuid(), CrossDepartment, new DashboardOverviewRequestDto(DepartmentId: _db.CollectionsId));
        var head = await OverviewAsync(_db.CollectionsHeadId, [Roles.DepartmentHead]);
        var headAskingForRegistration = await OverviewAsync(
            _db.CollectionsHeadId, [Roles.DepartmentHead], new DashboardOverviewRequestDto(DepartmentId: _db.RegistrationId));

        Assert.Equal((3, 1), (supervisorAll.Kpis.ClosedByAgentTickets, supervisorAll.Kpis.ClosedByAiTickets));
        Assert.Equal((1, 1), (supervisorCollections.Kpis.ClosedByAgentTickets, supervisorCollections.Kpis.ClosedByAiTickets));
        Assert.Equal((1, 1), (head.Kpis.ClosedByAgentTickets, head.Kpis.ClosedByAiTickets));
        Assert.Equal((0, 0), (headAskingForRegistration.Kpis.ClosedByAgentTickets, headAskingForRegistration.Kpis.ClosedByAiTickets));
        Assert.Equal(0, headAskingForRegistration.Kpis.NotReviewedTickets);
    }

    // ---------------------------------------------------------------
    // Caller scope: employee = own assigned tickets, manager = everyone's
    // ---------------------------------------------------------------

    private static readonly string[] EmployeeRoles = [Roles.DepartmentEmployee];
    private static readonly string[] ManagerRoles = [Roles.CsManager];

    /// <summary>A Closed ticket whose last assignee is <paramref name="assignee"/> (closure clears the live owner; the assignment history keeps who had it).</summary>
    private Ticket ClosedAssignedTo(Guid assignee, DateTime? closedAtUtc = null, bool reviewed = false, int? departmentId = null)
    {
        using var context = _db.CreateContext();
        var ticket = _db.AddTicket(context, departmentId ?? _db.CustomerServiceId, owner: assignee, status: TicketStatus.Closed, closedAtUtc: closedAtUtc ?? Now.AddDays(-2));
        _db.AddAssignmentHistory(context, ticket, assignee);
        if (reviewed)
        {
            _db.AddReview(context, ticket);
        }

        return ticket;
    }

    private (Guid Alice, Guid Bob) TwoEmployees()
    {
        using var context = _db.CreateContext();
        return (_db.AddEmployee(context, "Alice Employee"), _db.AddEmployee(context, "Bob Employee"));
    }

    [Fact]
    public async Task ClosureKpis_ForTwoEmployeesAndAManager_AreScopedByTheAuthenticatedCaller_AndReconcile()
    {
        var (alice, bob) = TwoEmployees();
        var manager = Guid.NewGuid();

        // Alice: 2 reviewed (one with several review records), 1 unreviewed, plus one closed outside the period.
        var aliceReviewed = ClosedAssignedTo(alice, reviewed: true);
        using (var w = _db.CreateContext()) { _db.AddReview(w, aliceReviewed, isTest: true); _db.AddReview(w, aliceReviewed, isTest: true, submit: false); }
        ClosedAssignedTo(alice, reviewed: true);
        ClosedAssignedTo(alice);
        ClosedAssignedTo(alice, closedAtUtc: Now.AddDays(-60), reviewed: true);
        // Bob (same department as Alice): 1 reviewed, 2 unreviewed, plus one outside the period.
        ClosedAssignedTo(bob, reviewed: true);
        ClosedAssignedTo(bob);
        ClosedAssignedTo(bob);
        ClosedAssignedTo(bob, closedAtUtc: Now.AddDays(-60));

        var forAlice = await OverviewAsync(alice, EmployeeRoles);
        var forBob = await OverviewAsync(bob, EmployeeRoles);
        var forManager = await OverviewAsync(manager, ManagerRoles);

        Assert.Equal((2, 1), (forAlice.Kpis.ReviewedTickets, forAlice.Kpis.NotReviewedTickets));
        Assert.Equal((1, 2), (forBob.Kpis.ReviewedTickets, forBob.Kpis.NotReviewedTickets));
        Assert.Equal((3, 3), (forManager.Kpis.ReviewedTickets, forManager.Kpis.NotReviewedTickets));

        // The browser cannot widen the scope: an owner filter naming someone else only narrows within the caller's own tickets.
        var aliceAskingForBob = await OverviewAsync(alice, EmployeeRoles, new DashboardOverviewRequestDto(OwnerEmployeeId: bob));
        Assert.Equal((0, 0), (aliceAskingForBob.Kpis.ReviewedTickets, aliceAskingForBob.Kpis.NotReviewedTickets));

        // Each card's list shows exactly what the card counted, for each caller.
        foreach (var (caller, roles, overview) in new[] { (alice, EmployeeRoles, forAlice), (bob, EmployeeRoles, forBob), (manager, ManagerRoles, forManager) })
        {
            var from = overview.Filters.DateFrom;
            var to = overview.Filters.DateTo;
            Assert.Equal(overview.Kpis.ReviewedTickets, await QueueCountAsync(caller, roles, TicketClosureCategory.Reviewed, from, to));
            Assert.Equal(overview.Kpis.NotReviewedTickets, await QueueCountAsync(caller, roles, TicketClosureCategory.NotReviewed, from, to));
        }
    }

    [Fact]
    public async Task ClosureKpis_EmployeeCountsOnlyTicketsAssignedToThem_NotTheirDepartmentsTickets_NorOnesAssignedToOthers()
    {
        var (alice, bob) = TwoEmployees();
        ClosedAssignedTo(alice);
        // A closed ticket in Alice's own department assigned to Bob, and an unassigned closed one.
        ClosedAssignedTo(bob);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        using (var w = _db.CreateContext())
        {
            w.UserDepartmentAssignments.Add(new UserDepartmentAssignment(alice, _db.CustomerServiceId, true, Now.AddDays(-50), null));
            w.SaveChanges();
        }

        var forAlice = await OverviewAsync(alice, EmployeeRoles);

        Assert.Equal((0, 1), (forAlice.Kpis.ReviewedTickets, forAlice.Kpis.NotReviewedTickets));
        Assert.Equal(1, await QueueCountAsync(alice, EmployeeRoles, TicketClosureCategory.NotReviewed, forAlice.Filters.DateFrom, forAlice.Filters.DateTo));
    }

    [Fact]
    public async Task ClosureKpis_CsAgentCountsOnlyTicketsAssignedToThem_NotOnesTheyWereOnlyResponsibleFor()
    {
        Guid agent, bob;
        using (var context = _db.CreateContext())
        {
            agent = _db.AddEmployee(context, "Agnes Agent");
            bob = _db.AddEmployee(context, "Bob Employee");
            _db.MakeCsAgent(context, agent);
        }

        string[] agentRoles = [Roles.CsAgent];
        ClosedAssignedTo(agent, reviewed: true);   // closed while assigned to the agent: theirs
        ClosedAssignedTo(agent);                   // theirs, unreviewed
        // The agent handed this one to a department employee who closed it: the agent may follow it, but it is Bob's.
        using (var context = _db.CreateContext())
        {
            var handedOver = _db.AddTicket(context, _db.CustomerServiceId, owner: bob, status: TicketStatus.Closed, closedAtUtc: Now.AddDays(-2));
            _db.AddAssignmentHistory(context, handedOver, agent);
            _db.AddAssignmentHistory(context, handedOver, bob);
        }

        var overview = await OverviewAsync(agent, agentRoles);

        Assert.Equal((1, 1), (overview.Kpis.ReviewedTickets, overview.Kpis.NotReviewedTickets));
        Assert.Equal(1, await QueueCountAsync(agent, agentRoles, TicketClosureCategory.NotReviewed, overview.Filters.DateFrom, overview.Filters.DateTo));
    }

    [Fact]
    public async Task ClosureKpis_ManagerOwnerFilter_MatchesTheClosedTicketsLastAssignee_NotTheClearedOwner()
    {
        var (alice, bob) = TwoEmployees();
        ClosedAssignedTo(alice, reviewed: true);
        ClosedAssignedTo(bob);

        var forAlice = await OverviewAsync(Guid.NewGuid(), ManagerRoles, new DashboardOverviewRequestDto(OwnerEmployeeId: alice));

        Assert.Equal((1, 0), (forAlice.Kpis.ReviewedTickets, forAlice.Kpis.NotReviewedTickets));
    }

    // ---------------------------------------------------------------
    // Drill-down parity
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, "Open")]
    [InlineData(true, "InProgress")]
    public async Task TicketQueueClosureCategoryFilter_ListsExactlyWhatEachCardCounted(bool testMode, string? dashboardStatusFilter)
    {
        var mode = testMode ? TestMode : ProductionMode;
        using (var context = _db.CreateContext())
        {
            var reviewedByAgent = ClosedTicket(_db.CollectionsId, TicketClosureAuditActions.ClosedByAgent, closedAtUtc: Now.AddDays(-2));
            var reviewedByAi = ClosedTicket(_db.CollectionsId, TicketClosureAuditActions.ClosedByAi, closedAtUtc: Now.AddDays(-3));
            ClosedTicket(_db.CollectionsId, TicketClosureAuditActions.ClosedByAi, closedAtUtc: Now.AddDays(-4));
            ClosedTicket(_db.CollectionsId, TicketClosureAuditActions.ClosedForCustomerInactivity, closedAtUtc: Now.AddDays(-5));
            ClosedTicket(_db.CollectionsId, null, createdAtUtc: Now.AddDays(-90), closedAtUtc: Now.AddDays(-60));      // closed outside the range
            ClosedTicket(_db.CollectionsId, TicketClosureAuditActions.ClosedByAgent, createdAtUtc: Now.AddDays(-90), closedAtUtc: Now.AddDays(-1)); // old ticket, recent closure — in
            _db.AddTicket(context, _db.CollectionsId, status: TicketStatus.InProgress);                                 // not closed
            ClosedTicket(_db.RegistrationId, TicketClosureAuditActions.ClosedByAgent);                                  // invisible to the head
            using var write = _db.CreateContext();
            _db.AddReview(write, reviewedByAgent, isTest: testMode);
            _db.AddReview(write, reviewedByAi, isTest: testMode);
            // A review of the other kind never counts in this mode.
            _db.AddReview(write, reviewedByAi, isTest: !testMode);
        }

        var head = _db.CollectionsHeadId;
        string[] roles = [Roles.DepartmentHead];
        var overview = await OverviewAsync(head, roles, new DashboardOverviewRequestDto(TicketStatus: dashboardStatusFilter), mode);
        var from = overview.Filters.DateFrom;
        var to = overview.Filters.DateTo;

        Assert.Equal((2, 3, 2, 2), (overview.Kpis.ReviewedTickets, overview.Kpis.NotReviewedTickets, overview.Kpis.ClosedByAiTickets, overview.Kpis.ClosedByAgentTickets));
        // The list the card links to: Closed status, the category, the closure range — same numbers.
        Assert.Equal(overview.Kpis.ReviewedTickets, await QueueCountAsync(head, roles, TicketClosureCategory.Reviewed, from, to, mode: mode));
        Assert.Equal(overview.Kpis.NotReviewedTickets, await QueueCountAsync(head, roles, TicketClosureCategory.NotReviewed, from, to, mode: mode));
        Assert.Equal(overview.Kpis.ClosedByAiTickets, await QueueCountAsync(head, roles, TicketClosureCategory.ClosedByAi, from, to, mode: mode));
        Assert.Equal(overview.Kpis.ClosedByAgentTickets, await QueueCountAsync(head, roles, TicketClosureCategory.ClosedByAgent, from, to, mode: mode));

        // The filter only ever yields Closed tickets, even if the list is asked for another status.
        Assert.Equal(0, await QueueCountAsync(head, roles, TicketClosureCategory.ClosedByAi, from, to, ticketStatus: nameof(TicketStatus.InProgress), mode: mode));
    }

    [Fact]
    public async Task TheListsRows_AreExactlyTheTicketsEachCardCounted_NotJustTheSameTotal()
    {
        var agent = ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAi);
        ClosedTicket(_db.CustomerServiceId, TicketClosureAuditActions.ClosedByAgent, closedAtUtc: Now.AddDays(-70));
        using var context = _db.CreateContext();
        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        var list = await _db.CreateQueryService(context, mode: ProductionMode).GetQueueAsync(
            Guid.NewGuid(), CrossDepartment,
            new TicketListRequestDto(null, null, null, "Closed", null, null, null, null, null, 1, 50,
                ClosureCategory: nameof(TicketClosureCategory.ClosedByAgent), ClosedFrom: overview.Filters.DateFrom, ClosedTo: overview.Filters.DateTo));

        Assert.Equal(overview.Kpis.ClosedByAgentTickets, list.TotalCount);
        Assert.Equal([agent.TicketId], list.Items.Select(t => t.TicketId).ToArray());
    }
}
