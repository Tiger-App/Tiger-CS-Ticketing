using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Tests.Ticketing.Dashboard;

/// <summary>
/// The Operational Dashboard (Dashboard Phase 1), end to end from the
/// application service down to real SQL: KPI semantics, every breakdown's
/// count and percentage, the explicit backlog-ageing boundaries, and — most
/// importantly — that every aggregate honours the exact same department
/// visibility as the ticket queue, and that no filter parameter can widen
/// it.
/// </summary>
public sealed class DashboardOverviewTests : IDisposable
{
    private static readonly DateTime Now = DashboardSqliteFixture.Now;
    private static readonly string[] CrossDepartment = [Roles.CsSupervisor];

    private readonly DashboardSqliteFixture _db = new();

    public void Dispose() => _db.Dispose();

    private Task<DashboardOverviewDto> OverviewAsync(
        Guid caller, string[] roles, DashboardOverviewRequestDto? request = null, DateTime? nowUtc = null)
    {
        using var context = _db.CreateContext();
        return _db.CreateService(context, nowUtc).GetOverviewAsync(caller, roles, request ?? new DashboardOverviewRequestDto());
    }

    private static int CountOf(IReadOnlyList<DashboardBreakdownItemDto> rows, string label) =>
        rows.Single(r => r.Label == label).Count;

    private static double PercentOf(IReadOnlyList<DashboardBreakdownItemDto> rows, string label) =>
        rows.Single(r => r.Label == label).Percentage;

    // ---------------------------------------------------------------
    // KPIs
    // ---------------------------------------------------------------

    [Fact]
    public async Task OpenTickets_CountsEveryNonTerminalLifecycleStatus_AndNothingElse()
    {
        using (var context = _db.CreateContext())
        {
            foreach (var status in Enum.GetValues<TicketStatus>())
            {
                _db.AddTicket(context, _db.CustomerServiceId, status: status);
            }
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        // Open, InProgress, PendingCustomer, PendingThirdParty — not Resolved, not Closed.
        Assert.Equal(4, overview.Kpis.OpenTickets);
    }

    [Fact]
    public async Task MyTickets_CountsEveryTicketCurrentlyAssignedToTheCaller_IncludingResolved()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, owner: _db.CsAgentId);
            _db.AddTicket(context, _db.CustomerServiceId, owner: _db.CsAgentId, status: TicketStatus.InProgress);
            _db.AddTicket(context, _db.CustomerServiceId, owner: _db.CsAgentId, status: TicketStatus.Resolved);
            _db.AddTicket(context, _db.CustomerServiceId, owner: _db.CollectionsHeadId);
            _db.AddTicket(context, _db.CustomerServiceId);
        }

        var overview = await OverviewAsync(_db.CsAgentId, [Roles.CsAgent]);

        // Open + InProgress + Resolved (awaiting Close) — what the My Tickets list shows; other owners' and unowned tickets excluded.
        Assert.Equal(3, overview.Kpis.MyTickets);
    }

    [Fact]
    public async Task InDepartmentQueue_CountsOwnerlessActiveTickets_InVisibleDepartmentsOnly()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CollectionsId);
            _db.AddTicket(context, _db.CollectionsId);
            // Owned tickets are being worked, not queued — whatever their active status.
            _db.AddTicket(context, _db.CollectionsId, owner: _db.CollectionsHeadId);
            _db.AddTicket(context, _db.CollectionsId, owner: _db.CollectionsHeadId, status: TicketStatus.PendingThirdParty);
            _db.AddTicket(context, _db.CollectionsId, status: TicketStatus.Closed);
            _db.AddTicket(context, _db.RegistrationId);
            _db.AddTicket(context, _db.RegistrationId);
        }

        var head = await OverviewAsync(_db.CollectionsHeadId, [Roles.DepartmentHead]);
        var supervisor = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(2, head.Kpis.InDepartmentQueue);
        Assert.Equal(4, supervisor.Kpis.InDepartmentQueue);
    }

    [Fact]
    public async Task SlaBreached_CountsActiveTicketsInBreachedSlaState()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, slaBreached: true);
            _db.AddTicket(context, _db.CustomerServiceId, slaBreached: true, status: TicketStatus.PendingCustomer);
            _db.AddTicket(context, _db.CustomerServiceId, slaBreached: true, status: TicketStatus.Closed);
            _db.AddTicket(context, _db.CustomerServiceId);
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(2, overview.Kpis.SlaBreached);
    }

    [Fact]
    public async Task DueToday_IsTheUtcCalendarDayOfTheCurrentUnbreachedResolutionDeadline_BoundariesInclusiveStartExclusiveEnd()
    {
        var todayStart = Now.Date;
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, resolutionDueAtUtc: todayStart);                       // 00:00:00 today — in
            _db.AddTicket(context, _db.CustomerServiceId, resolutionDueAtUtc: todayStart.AddDays(1).AddSeconds(-1)); // 23:59:59 today — in
            _db.AddTicket(context, _db.CustomerServiceId, resolutionDueAtUtc: todayStart.AddDays(1));            // 00:00:00 tomorrow — out
            _db.AddTicket(context, _db.CustomerServiceId, resolutionDueAtUtc: todayStart.AddSeconds(-1));        // 23:59:59 yesterday — out
            _db.AddTicket(context, _db.CustomerServiceId, resolutionDueAtUtc: Now, slaBreached: true);           // breached — SLA Breached, not due
            _db.AddTicket(context, _db.CustomerServiceId, resolutionDueAtUtc: Now, status: TicketStatus.Resolved); // not active — out
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(2, overview.Kpis.DueToday);
    }

    [Fact]
    public async Task PendingApproval_CountsOnlyPendingApprovalsTheCallerIsAuthorizedToAction()
    {
        var csManager = Guid.NewGuid();
        using (var context = _db.CreateContext())
        {
            var collectionsTicket = _db.AddTicket(context, _db.CollectionsId, requestTypeId: _db.CollectionsRequestTypeId);
            var csTicket = _db.AddTicket(context, _db.CustomerServiceId, requestTypeId: _db.CsRequestTypeId);
            var registrationTicket = _db.AddTicket(context, _db.RegistrationId);

            // Department-targeted at Collections: a Collections member with a department role decides.
            _db.AddPendingApproval(context, collectionsTicket,
                RequestTypeApprovalRequirement.ForDepartment(_db.CollectionsRequestTypeId, ApprovalType.AccountingApproval, _db.CollectionsId), _db.CsAgentId);
            // Role-targeted at CS Manager.
            _db.AddPendingApproval(context, csTicket,
                RequestTypeApprovalRequirement.ForRole(_db.CsRequestTypeId, ApprovalType.CustomerServiceApproval, Roles.CsManager), _db.CsAgentId);
            // Employee-targeted at the Registration employee — and already decided, so never pending.
            var decided = _db.AddPendingApproval(context, registrationTicket,
                RequestTypeApprovalRequirement.ForEmployee(_db.CsRequestTypeId, ApprovalType.AccountingApproval, _db.RegistrationEmployeeId), _db.CsAgentId);
            decided.Approve(_db.RegistrationEmployeeId, Now.AddHours(-1), null);
            context.SaveChanges();
        }

        var head = await OverviewAsync(_db.CollectionsHeadId, [Roles.DepartmentHead]);
        var manager = await OverviewAsync(csManager, [Roles.CsManager]);
        var registrar = await OverviewAsync(_db.RegistrationEmployeeId, [Roles.DepartmentEmployee]);
        var agent = await OverviewAsync(_db.CsAgentId, [Roles.CsAgent]);
        var administrator = await OverviewAsync(Guid.NewGuid(), [Roles.SystemAdministrator]);

        Assert.Equal(1, head.Kpis.PendingApproval);          // the Collections department target
        Assert.Equal(1, manager.Kpis.PendingApproval);       // the CS Manager role target
        Assert.Equal(0, registrar.Kpis.PendingApproval);     // their employee target was already decided
        Assert.Equal(0, agent.Kpis.PendingApproval);         // sees the tickets, may action none of them
        Assert.Equal(2, administrator.Kpis.PendingApproval); // the override: every still-pending cycle
    }

    // ---------------------------------------------------------------
    // Breakdowns
    // ---------------------------------------------------------------

    [Fact]
    public async Task VolumeByChannel_GroupsByOriginatingChannel_WithPercentagesOfTheFilteredTotal()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, channelId: WellKnownChannels.Phone);
            _db.AddTicket(context, _db.CustomerServiceId, channelId: WellKnownChannels.Phone);
            _db.AddTicket(context, _db.CustomerServiceId, channelId: WellKnownChannels.WhatsApp);
            _db.AddTicket(context, _db.CustomerServiceId);
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(4, overview.VolumeTotal);
        Assert.Equal(2, CountOf(overview.VolumeByChannel, "Phone"));
        Assert.Equal(50.0, PercentOf(overview.VolumeByChannel, "Phone"));
        Assert.Equal(1, CountOf(overview.VolumeByChannel, "WhatsApp"));
        Assert.Equal(25.0, PercentOf(overview.VolumeByChannel, "WhatsApp"));
        // A ticket with no originating interaction is shown honestly, never
        // folded into a real channel, and is not a drill-down (null key).
        var unrecorded = overview.VolumeByChannel.Single(r => r.Key is null);
        Assert.Equal(1, unrecorded.Count);
        Assert.Equal("Phone", overview.VolumeByChannel[0].Label);
        Assert.Equal(WellKnownChannels.Phone.ToString(), overview.VolumeByChannel[0].Key);
    }

    [Fact]
    public async Task VolumeByRequestType_UsesTheConfiguredRequestTypeNames()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, requestTypeId: _db.CsRequestTypeId);
            _db.AddTicket(context, _db.CustomerServiceId, requestTypeId: _db.CsRequestTypeId);
            _db.AddTicket(context, _db.CustomerServiceId, requestTypeId: _db.CsRequestTypeId);
            _db.AddTicket(context, _db.CollectionsId, requestTypeId: _db.CollectionsRequestTypeId);
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(3, CountOf(overview.VolumeByRequestType, "Complaint"));
        Assert.Equal(75.0, PercentOf(overview.VolumeByRequestType, "Complaint"));
        Assert.Equal(1, CountOf(overview.VolumeByRequestType, "Payment Reminder"));
        Assert.Equal(_db.CsRequestTypeId.ToString(), overview.VolumeByRequestType[0].Key);
    }

    [Fact]
    public async Task VolumeByDepartment_UsesTheCurrentResponsibleDepartment_NotTheOriginatingOne()
    {
        using (var context = _db.CreateContext())
        {
            // Originated in Customer Service, transferred to Collections.
            _db.AddTicket(context, _db.CustomerServiceId, transferToDepartmentId: _db.CollectionsId);
            _db.AddTicket(context, _db.CustomerServiceId, transferToDepartmentId: _db.CollectionsId);
            _db.AddTicket(context, _db.CustomerServiceId);
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(2, CountOf(overview.VolumeByDepartment, "Collections"));
        Assert.Equal(1, CountOf(overview.VolumeByDepartment, "Customer Service"));
        Assert.Equal(_db.CollectionsId.ToString(), overview.VolumeByDepartment[0].Key);
    }

    [Fact]
    public async Task StatusBreakdown_UsesOnlyTheLifecyclesOwnStatuses()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.Resolved);
            _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.Resolved);
            _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.InProgress);
            _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.Closed);
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(2, CountOf(overview.StatusBreakdown, "Resolved"));
        Assert.Equal(50.0, PercentOf(overview.StatusBreakdown, "Resolved"));
        Assert.Equal(1, CountOf(overview.StatusBreakdown, "InProgress"));
        Assert.Equal(1, CountOf(overview.StatusBreakdown, "Closed"));
        Assert.All(overview.StatusBreakdown, r => Assert.True(Enum.TryParse<TicketStatus>(r.Key, out _)));
        // Not every status is present — only the ones with data; nothing invented.
        Assert.Equal(3, overview.StatusBreakdown.Count);
    }

    [Fact]
    public async Task PriorityBreakdown_UsesThePriorityTableNames_AndReportsUnclassifiedHonestly()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, priorityId: (byte)PriorityLevel.Critical);
            _db.AddTicket(context, _db.CustomerServiceId, priorityId: (byte)PriorityLevel.High);
            _db.AddTicket(context, _db.CustomerServiceId, priorityId: (byte)PriorityLevel.High);
            _db.AddTicket(context, _db.CustomerServiceId, priorityId: null);
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(2, CountOf(overview.PriorityBreakdown, "High"));
        Assert.Equal(50.0, PercentOf(overview.PriorityBreakdown, "High"));
        Assert.Equal(1, CountOf(overview.PriorityBreakdown, "Critical"));
        var unclassified = overview.PriorityBreakdown.Single(r => r.Key is null);
        Assert.Equal(1, unclassified.Count);
        Assert.DoesNotContain(overview.PriorityBreakdown, r => r.Label == "Medium");
    }

    [Fact]
    public async Task Percentages_AreZeroNeverNaN_WhenNothingMatches()
    {
        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(0, overview.VolumeTotal);
        Assert.Empty(overview.StatusBreakdown);
        Assert.Equal(4, overview.BacklogAgeing.Count);
        Assert.All(overview.BacklogAgeing, r => Assert.Equal(0.0, r.Percentage));
        Assert.Equal(0, DashboardAppService.Percentage(0, 0));
        Assert.Equal(33.3, DashboardAppService.Percentage(1, 3));
        Assert.Equal(66.7, DashboardAppService.Percentage(2, 3));
    }

    [Fact]
    public async Task VolumeBreakdowns_CoverTheDateRangeOnly_WhileBacklogKpisCoverEveryActiveTicket()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddDays(-2));
            _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddDays(-90)); // old but still open
            _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddDays(-90), status: TicketStatus.Closed);
        }

        var defaulted = await OverviewAsync(Guid.NewGuid(), CrossDepartment);
        var explicitRange = await OverviewAsync(Guid.NewGuid(), CrossDepartment,
            new DashboardOverviewRequestDto(DateFrom: new DateOnly(2026, 6, 1), DateTo: new DateOnly(2026, 6, 30)));

        // Default period: the last 30 days, today included.
        Assert.Equal(new DateOnly(2026, 8, 12), defaulted.Filters.DateFrom);
        Assert.Equal(new DateOnly(2026, 9, 10), defaulted.Filters.DateTo);
        Assert.Equal(1, defaulted.VolumeTotal);
        // The 90-day-old open ticket is still backlog — never hidden by the range.
        Assert.Equal(2, defaulted.Kpis.OpenTickets);
        Assert.Equal(2, defaulted.BacklogAgeing.Sum(r => r.Count));

        Assert.Equal(2, explicitRange.VolumeTotal);
        Assert.Equal(2, explicitRange.Kpis.OpenTickets);
    }

    // ---------------------------------------------------------------
    // Open Backlog Ageing — explicit boundaries
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(23.99, nameof(BacklogAgeBucket.Under24Hours))]
    [InlineData(24.0, nameof(BacklogAgeBucket.OneToThreeDays))]     // exactly 24h → 1–3 days
    [InlineData(48.0, nameof(BacklogAgeBucket.OneToThreeDays))]
    [InlineData(72.0, nameof(BacklogAgeBucket.ThreeToSevenDays))]   // exactly 72h → 3–7 days
    [InlineData(120.0, nameof(BacklogAgeBucket.ThreeToSevenDays))]
    [InlineData(168.0, nameof(BacklogAgeBucket.OverSevenDays))]     // exactly 168h → > 7 days
    [InlineData(400.0, nameof(BacklogAgeBucket.OverSevenDays))]
    public async Task BacklogAgeing_PlacesAnActiveTicketInExactlyOneBucket_ByExplicitHalfOpenBoundaries(double ageHours, string expectedBucket)
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddHours(-ageHours));
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(1, overview.BacklogAgeing.Single(r => r.Key == expectedBucket).Count);
        Assert.Equal(1, overview.BacklogAgeing.Sum(r => r.Count));
        Assert.Equal(100.0, overview.BacklogAgeing.Single(r => r.Key == expectedBucket).Percentage);
        // The in-memory twin agrees with the SQL, boundary for boundary.
        Assert.Equal(Enum.Parse<BacklogAgeBucket>(expectedBucket), BacklogAgeBoundaries.BucketOf(Now.AddHours(-ageHours), Now));
    }

    [Fact]
    public async Task BacklogAgeing_ExcludesResolvedAndClosedTickets()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddDays(-10), status: TicketStatus.Resolved);
            _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddDays(-10), status: TicketStatus.Closed);
            _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddDays(-10), status: TicketStatus.InProgress);
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(1, overview.BacklogAgeing.Sum(r => r.Count));
        Assert.Equal(1, overview.BacklogAgeing.Single(r => r.Key == nameof(BacklogAgeBucket.OverSevenDays)).Count);
        Assert.Equal(
            [
                nameof(BacklogAgeBucket.Under24Hours), nameof(BacklogAgeBucket.OneToThreeDays),
                nameof(BacklogAgeBucket.ThreeToSevenDays), nameof(BacklogAgeBucket.OverSevenDays)
            ],
            overview.BacklogAgeing.Select(r => r.Key ?? "").ToArray());
    }

    // ---------------------------------------------------------------
    // Authorization / visibility
    // ---------------------------------------------------------------

    [Fact]
    public async Task DepartmentUser_NeverSeesAggregateCountsForUnauthorizedDepartments()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CollectionsId, channelId: WellKnownChannels.Phone, requestTypeId: _db.CollectionsRequestTypeId, slaBreached: true);
            _db.AddTicket(context, _db.RegistrationId, channelId: WellKnownChannels.Phone, slaBreached: true, priorityId: (byte)PriorityLevel.Critical);
            _db.AddTicket(context, _db.RegistrationId, channelId: WellKnownChannels.WhatsApp);
        }

        var overview = await OverviewAsync(_db.CollectionsHeadId, [Roles.DepartmentHead]);

        Assert.Equal(1, overview.Kpis.OpenTickets);
        Assert.Equal(1, overview.Kpis.SlaBreached);
        Assert.Equal(1, overview.VolumeTotal);
        Assert.Equal(1, CountOf(overview.VolumeByChannel, "Phone"));
        Assert.DoesNotContain(overview.VolumeByChannel, r => r.Label == "WhatsApp");
        Assert.Single(overview.VolumeByDepartment);
        Assert.Equal("Collections", overview.VolumeByDepartment[0].Label);
        Assert.DoesNotContain(overview.PriorityBreakdown, r => r.Label == "Critical");
        Assert.Single(overview.RecentTickets);
        Assert.Equal(1, overview.BacklogAgeing.Sum(r => r.Count));
        // The pickers offer only what the caller may see.
        Assert.Equal(["Collections"], overview.FilterOptions.Departments.Select(o => o.Label).ToArray());
        Assert.Equal(["Hadi Head"], overview.FilterOptions.Agents.Select(o => o.Label).ToArray());
        Assert.Equal(["Payment Reminder"], overview.FilterOptions.RequestTypes.Select(o => o.Label).ToArray());
    }

    [Theory]
    [InlineData(Roles.CsSupervisor)]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.GeneralManager)]
    [InlineData(Roles.ChairmanCeo)]
    [InlineData(Roles.SystemAdministrator)]
    public async Task CrossDepartmentViewRoles_SeeEveryDepartment_ExactlyAsTheTicketQueueDoes(string role)
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId);
            _db.AddTicket(context, _db.CollectionsId);
            _db.AddTicket(context, _db.RegistrationId);
        }

        var overview = await OverviewAsync(Guid.NewGuid(), [role]);

        Assert.Equal(3, overview.Kpis.OpenTickets);
        Assert.Equal(3, overview.VolumeByDepartment.Count);
        Assert.Equal(3, overview.FilterOptions.Departments.Count);
    }

    [Theory]
    [InlineData(Roles.DepartmentHead)]
    [InlineData(Roles.ReportingUser)]
    public async Task DepartmentScopedRoles_SeeOnlyTheirOwnMemberships(string role)
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId);
            _db.AddTicket(context, _db.CollectionsId);
            _db.AddTicket(context, _db.RegistrationId);
        }

        var collections = await OverviewAsync(_db.CollectionsHeadId, [role]);
        var nobody = await OverviewAsync(Guid.NewGuid(), [role]);

        Assert.Equal(1, collections.Kpis.OpenTickets);
        Assert.Equal(0, nobody.Kpis.OpenTickets);
        Assert.Empty(nobody.VolumeByDepartment);
        Assert.Empty(nobody.FilterOptions.Departments);
    }

    [Fact]
    public async Task ADepartmentEmployee_DashboardCountsOnlyTicketsAssignedToThem_NotTheirDepartmentsQueue()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CollectionsId);                                              // own department, unassigned
            _db.AddTicket(context, _db.CollectionsId, owner: _db.LifecycleWorkerId);                // own department, a colleague's
            _db.AddTicket(context, _db.CustomerServiceId, owner: _db.CollectionsHeadId);            // another department, assigned to the caller
            _db.AddTicket(context, _db.RegistrationId);                                             // another department, unassigned
        }

        var mine = await OverviewAsync(_db.CollectionsHeadId, [Roles.DepartmentEmployee]);
        var nobody = await OverviewAsync(Guid.NewGuid(), [Roles.DepartmentEmployee]);

        Assert.Equal(1, mine.Kpis.OpenTickets);
        Assert.Equal(1, mine.Kpis.MyTickets);
        Assert.Equal(0, mine.Kpis.InDepartmentQueue);   // the department's unassigned queue is not an employee's to see
        Assert.Equal(0, nobody.Kpis.OpenTickets);
    }

    [Fact]
    public async Task DepartmentFilter_CannotWidenTheCallersScope()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CollectionsId);
            _db.AddTicket(context, _db.RegistrationId, slaBreached: true);
            _db.AddTicket(context, _db.RegistrationId);
        }

        var outside = await OverviewAsync(_db.CollectionsHeadId, [Roles.DepartmentHead],
            new DashboardOverviewRequestDto(DepartmentId: _db.RegistrationId));
        var inside = await OverviewAsync(_db.CollectionsHeadId, [Roles.DepartmentHead],
            new DashboardOverviewRequestDto(DepartmentId: _db.CollectionsId));
        var supervisor = await OverviewAsync(Guid.NewGuid(), CrossDepartment,
            new DashboardOverviewRequestDto(DepartmentId: _db.RegistrationId));

        // A department outside the scope yields nothing — not the other department's numbers.
        Assert.Equal(0, outside.Kpis.OpenTickets);
        Assert.Equal(0, outside.Kpis.SlaBreached);
        Assert.Equal(0, outside.VolumeTotal);
        Assert.Empty(outside.RecentTickets);
        Assert.Empty(outside.FilterOptions.Agents);
        Assert.Equal(1, inside.Kpis.OpenTickets);
        Assert.Equal(2, supervisor.Kpis.OpenTickets);
        Assert.Equal(["Rana Registrar"], supervisor.FilterOptions.Agents.Select(o => o.Label).ToArray());
    }

    [Theory]
    [InlineData(Roles.CsManager)]
    [InlineData(Roles.CsSupervisor)]
    public async Task CsManagerAndSupervisor_AgentPicker_OffersCsAgentsAndCallCenterStaff_AcrossDepartments(string role)
    {
        // Agreed model: Call Center agents hold the CS Agent set. A cross-department caller is
        // offered every active member of every department, so a Call Center agent appears beside
        // the Customer Service agent and the other departments' staff.
        var callCenterAgentId = Guid.NewGuid();
        using (var context = _db.CreateContext())
        {
            var callCenter = new Department("Call Center", "CC");
            context.Departments.Add(callCenter);
            context.SaveChanges();
            context.Users.Add(new TigerCS.Infrastructure.Identity.ApplicationUser
            {
                Id = callCenterAgentId,
                UserName = $"user-{callCenterAgentId:N}",
                NormalizedUserName = $"USER-{callCenterAgentId:N}",
                Email = $"user-{callCenterAgentId:N}@example.test",
                SecurityStamp = Guid.NewGuid().ToString()
            });
            context.Employees.Add(new Employee(callCenterAgentId, "Bilal Caller", isGeynessStaff: false, Now.AddDays(-60)));
            context.SaveChanges();
            context.UserDepartmentAssignments.Add(new UserDepartmentAssignment(callCenterAgentId, callCenter.DepartmentId, true, Now.AddDays(-50), null));
            context.SaveChanges();
        }

        var overview = await OverviewAsync(Guid.NewGuid(), [role]);

        var agents = overview.FilterOptions.Agents.Select(o => o.Label).ToArray();
        Assert.Contains("Amal Agent", agents);
        Assert.Contains("Bilal Caller", agents);
        Assert.Contains("Hadi Head", agents);
        Assert.Contains("Rana Registrar", agents);

        // The picker follows the department filter: Call Center only offers its own people.
        int callCenterId;
        using (var context = _db.CreateContext())
        {
            callCenterId = context.Departments.Single(d => d.Code == "CC").DepartmentId;
        }

        var narrowed = await OverviewAsync(Guid.NewGuid(), [role], new DashboardOverviewRequestDto(DepartmentId: callCenterId));
        Assert.Equal(["Bilal Caller"], narrowed.FilterOptions.Agents.Select(o => o.Label).ToArray());
    }

    [Fact]
    public async Task OtherFilters_NarrowEveryWidget_ButNeverBypassScope()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CollectionsId, owner: _db.CollectionsHeadId, priorityId: (byte)PriorityLevel.High, channelId: WellKnownChannels.Phone);
            _db.AddTicket(context, _db.CollectionsId, priorityId: (byte)PriorityLevel.Low, channelId: WellKnownChannels.Phone);
            _db.AddTicket(context, _db.RegistrationId, owner: _db.CollectionsHeadId, priorityId: (byte)PriorityLevel.High, channelId: WellKnownChannels.Phone);
        }

        var byOwner = await OverviewAsync(_db.CollectionsHeadId, [Roles.DepartmentHead],
            new DashboardOverviewRequestDto(OwnerEmployeeId: _db.CollectionsHeadId));
        var byPriority = await OverviewAsync(_db.CollectionsHeadId, [Roles.DepartmentHead],
            new DashboardOverviewRequestDto(PriorityId: (byte)PriorityLevel.High, ChannelId: WellKnownChannels.Phone));

        // The Registration ticket the head owns is visible too: a ticket assigned to you is always yours to see (My Tickets), whatever
        // department it sits in. Nothing else outside the head's departments is visible - see the unowned-Registration case below.
        Assert.Equal(2, byOwner.Kpis.OpenTickets);
        Assert.Equal(2, byOwner.Kpis.MyTickets);
        // High + Phone: the head's own Collections ticket and the Registration ticket assigned to them; the filters narrow, never widen.
        Assert.Equal(2, byPriority.VolumeTotal);
        Assert.Equal(2, CountOf(byPriority.VolumeByChannel, "Phone"));
    }

    // ---------------------------------------------------------------
    // Recent / Critical
    // ---------------------------------------------------------------

    [Fact]
    public async Task RecentTickets_AreNewestFirstRegardlessOfSlaOrPriority_WithNamesResolved()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddHours(-5), priorityId: (byte)PriorityLevel.Low, customerName: "Newest Low");
            _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddHours(-9), priorityId: (byte)PriorityLevel.High, customerName: "High", owner: _db.CsAgentId, requestTypeId: _db.CsRequestTypeId);
            _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddHours(-8), priorityId: (byte)PriorityLevel.Low, customerName: "Due soon", resolutionDueAtUtc: Now.AddHours(1));
            _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddHours(-7), priorityId: (byte)PriorityLevel.Low, customerName: "Breached", slaBreached: true, resolutionDueAtUtc: Now.AddHours(-3));
            _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddHours(-6), priorityId: (byte)PriorityLevel.Low, customerName: "Older Low");
            _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddHours(-1), priorityId: (byte)PriorityLevel.Critical, customerName: "Closed critical", status: TicketStatus.Closed, slaBreached: true);
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(["Newest Low", "Older Low", "Breached", "Due soon", "High"], overview.RecentTickets.Select(r => r.CustomerName ?? "").ToArray());
        var high = overview.RecentTickets.Single(r => r.CustomerName == "High");
        Assert.Equal("Customer Service", high.DepartmentName);
        Assert.Equal("Complaint", high.RequestTypeName);
        Assert.Equal("Amal Agent", high.OwnerName);
        Assert.Equal("Tiger Tower", high.ProjectName);
        Assert.StartsWith("U-", high.UnitNumber);
        Assert.Equal(Now.AddHours(1), overview.RecentTickets.Single(r => r.CustomerName == "Due soon").SlaDueAtUtc);
    }

    [Fact]
    public async Task RecentTickets_TiedCreatedAt_BreakByTicketIdDescending_AndCarryDepartmentCode()
    {
        using (var context = _db.CreateContext())
        {
            for (var i = 0; i < 3; i++)
            {
                _db.AddTicket(context, _db.CustomerServiceId, createdAtUtc: Now.AddHours(-1), customerName: $"Tie {i}");
            }
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(["Tie 2", "Tie 1", "Tie 0"], overview.RecentTickets.Select(r => r.CustomerName ?? "").ToArray());
        Assert.Equal(overview.RecentTickets.Select(r => r.TicketId).OrderByDescending(id => id), overview.RecentTickets.Select(r => r.TicketId));
        Assert.All(overview.RecentTickets, r => Assert.Equal("CS", r.DepartmentCode));
    }

    [Fact]
    public async Task RecentTickets_AreBounded()
    {
        using (var context = _db.CreateContext())
        {
            for (var i = 0; i < DashboardAppService.RecentLimit + 5; i++)
            {
                _db.AddTicket(context, _db.CustomerServiceId);
            }
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(DashboardAppService.RecentLimit, overview.RecentTickets.Count);
        Assert.Equal(DashboardAppService.RecentLimit + 5, overview.Kpis.OpenTickets);
    }

    // ---------------------------------------------------------------
    // Drill-down: the ticket queue's new filters agree with the KPIs
    // ---------------------------------------------------------------

    [Fact]
    public async Task TicketQueueDrilldownFilters_ProduceExactlyTheTicketsEachKpiAndBucketCounted()
    {
        var todayStart = Now.Date;
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CollectionsId, createdAtUtc: Now.AddHours(-2), channelId: WellKnownChannels.WhatsApp);                                  // <24h, queue
            _db.AddTicket(context, _db.CollectionsId, createdAtUtc: Now.AddHours(-30), owner: _db.CollectionsHeadId, slaBreached: true);                  // 1–3d, breached
            _db.AddTicket(context, _db.CollectionsId, createdAtUtc: Now.AddHours(-100), resolutionDueAtUtc: todayStart.AddHours(15));                     // 3–7d, due today
            _db.AddTicket(context, _db.CollectionsId, createdAtUtc: Now.AddDays(-20), status: TicketStatus.Closed, channelId: WellKnownChannels.WhatsApp); // terminal
            var approvalTicket = _db.AddTicket(context, _db.CollectionsId, createdAtUtc: Now.AddDays(-9), requestTypeId: _db.CollectionsRequestTypeId);   // >7d, approval
            _db.AddPendingApproval(context, approvalTicket,
                RequestTypeApprovalRequirement.ForDepartment(_db.CollectionsRequestTypeId, ApprovalType.AccountingApproval, _db.CollectionsId), _db.CsAgentId);
            _db.AddTicket(context, _db.RegistrationId, createdAtUtc: Now.AddHours(-2)); // invisible to the Collections head
        }

        var overview = await OverviewAsync(_db.CollectionsHeadId, [Roles.DepartmentHead]);

        using var queryContext = _db.CreateContext();
        var queue = _db.CreateQueryService(queryContext);
        async Task<int> CountAsync(TicketListRequestDto request) =>
            (await queue.GetQueueAsync(_db.CollectionsHeadId, [Roles.DepartmentHead], request)).TotalCount;
        TicketListRequestDto Request(
            bool? activeOnly = null, bool? inQueue = null, bool? breached = null, bool? dueToday = null,
            string? backlogAge = null, bool? pendingApproval = null, byte? channelId = null, int? requestTypeId = null,
            Guid? owner = null, DateOnly? from = null, DateOnly? to = null) =>
            new(null, null, null, null, null, owner, null, null, null, 1, 50,
                ChannelId: channelId, RequestTypeId: requestTypeId, ActiveOnly: activeOnly, InDepartmentQueue: inQueue,
                SlaBreached: breached, DueToday: dueToday, BacklogAge: backlogAge, PendingApproval: pendingApproval,
                CreatedFrom: from, CreatedTo: to);

        Assert.Equal(overview.Kpis.OpenTickets, await CountAsync(Request(activeOnly: true)));
        Assert.Equal(4, overview.Kpis.OpenTickets);
        Assert.Equal(overview.Kpis.MyTickets, await CountAsync(Request(activeOnly: true, owner: _db.CollectionsHeadId)));
        Assert.Equal(overview.Kpis.InDepartmentQueue, await CountAsync(Request(inQueue: true)));
        Assert.Equal(3, overview.Kpis.InDepartmentQueue);
        Assert.Equal(overview.Kpis.SlaBreached, await CountAsync(Request(breached: true)));
        Assert.Equal(1, overview.Kpis.SlaBreached);
        Assert.Equal(overview.Kpis.DueToday, await CountAsync(Request(dueToday: true)));
        Assert.Equal(1, overview.Kpis.DueToday);
        Assert.Equal(overview.Kpis.PendingApproval, await CountAsync(Request(pendingApproval: true)));
        Assert.Equal(1, overview.Kpis.PendingApproval);

        foreach (var bucket in overview.BacklogAgeing)
        {
            Assert.Equal(bucket.Count, await CountAsync(Request(backlogAge: bucket.Key)));
            Assert.Equal(1, bucket.Count);
        }

        // Volume dimensions: channel and request type, within the date range.
        var whatsApp = overview.VolumeByChannel.Single(r => r.Label == "WhatsApp");
        Assert.Equal(whatsApp.Count, await CountAsync(Request(channelId: WellKnownChannels.WhatsApp, from: overview.Filters.DateFrom, to: overview.Filters.DateTo)));
        Assert.Equal(2, whatsApp.Count);
        var reminder = overview.VolumeByRequestType.Single(r => r.Label == "Payment Reminder");
        Assert.Equal(reminder.Count, await CountAsync(Request(requestTypeId: _db.CollectionsRequestTypeId, from: overview.Filters.DateFrom, to: overview.Filters.DateTo)));
    }

    [Fact]
    public async Task TicketQueuePendingApprovalFilter_IsScopedToTheCallersOwnApproverRights()
    {
        using (var context = _db.CreateContext())
        {
            var ticket = _db.AddTicket(context, _db.CollectionsId, requestTypeId: _db.CollectionsRequestTypeId);
            _db.AddPendingApproval(context, ticket,
                RequestTypeApprovalRequirement.ForRole(_db.CollectionsRequestTypeId, ApprovalType.AccountingApproval, Roles.CsManager), _db.CsAgentId);
        }

        using var context2 = _db.CreateContext();
        var queue = _db.CreateQueryService(context2);
        var request = new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 50, PendingApproval: true);

        var manager = await queue.GetQueueAsync(Guid.NewGuid(), [Roles.CsManager], request);
        var agent = await queue.GetQueueAsync(Guid.NewGuid(), [Roles.CsAgent], request);
        var head = await queue.GetQueueAsync(_db.CollectionsHeadId, [Roles.DepartmentHead], request);

        Assert.Equal(1, manager.TotalCount);
        Assert.Equal(0, agent.TotalCount);
        Assert.Equal(0, head.TotalCount);
    }

    // ---------------------------------------------------------------
    // The retired PendingThirdParty status
    // ---------------------------------------------------------------

    [Fact]
    public async Task StatusFilterOptions_OfferTheActiveStatusesOnly_NeverTheRetiredOne()
    {
        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(
            ["Open", "InProgress", "PendingCustomer", "Resolved", "Closed"],
            overview.FilterOptions.Statuses.Select(o => o.Id).ToArray());
        Assert.DoesNotContain(overview.FilterOptions.Statuses, o => o.Id == nameof(TicketStatus.PendingThirdParty));
    }

    [Fact]
    public async Task HistoricalPendingThirdPartyTickets_StayReadable_AndKeepReportingTheirRealStatus()
    {
        // Retiring the status must not rewrite history: a stored row reads
        // back as itself, counts as the active work it still is, and appears
        // in the breakdown under its own name.
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.PendingThirdParty);
            _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.InProgress);
        }

        var overview = await OverviewAsync(Guid.NewGuid(), CrossDepartment);

        Assert.Equal(1, CountOf(overview.StatusBreakdown, nameof(TicketStatus.PendingThirdParty)));

        // Unfinished work is still unfinished: a legacy pending ticket stays
        // in the active set, so it never vanishes from the queue someone would
        // use to return it to InProgress.
        Assert.Equal(2, overview.Kpis.OpenTickets);
    }

    [Fact]
    public async Task HistoricalPendingThirdPartyTicket_IsStillReturnedByTheQueue()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.PendingThirdParty);
        }

        using var readContext = _db.CreateContext();
        var queue = _db.CreateQueryService(readContext);

        var byStatus = await queue.GetQueueAsync(
            Guid.NewGuid(), CrossDepartment,
            new TicketListRequestDto(null, null, null, nameof(TicketStatus.PendingThirdParty), null, null, null, null, null, 1, 20));

        // Not offered as a filter OPTION any more, but a caller that names it
        // — or a saved link that already did — still gets the historical
        // ticket back rather than an error or an empty page.
        Assert.Equal(1, byStatus.TotalCount);
        Assert.Equal(nameof(TicketStatus.PendingThirdParty), byStatus.Items[0].TicketStatus);
    }

    [Fact]
    public void ResolveScope_NarrowsOnly()
    {
        Assert.Null(DashboardAppService.ResolveScope(null, null));
        Assert.Equal([7], DashboardAppService.ResolveScope(null, 7)!);
        Assert.Equal([1, 2], DashboardAppService.ResolveScope([1, 2], null)!);
        Assert.Equal([2], DashboardAppService.ResolveScope([1, 2], 2)!);
        Assert.Empty(DashboardAppService.ResolveScope([1, 2], 9)!);
    }

    [Fact]
    public void ResolvePeriod_DefaultsToTheLastThirtyDays_AndKeepsAReversedRangeUsable()
    {
        var today = new DateOnly(2026, 9, 10);

        Assert.Equal((new DateOnly(2026, 8, 12), today), DashboardAppService.ResolvePeriod(null, null, today));
        Assert.Equal((new DateOnly(2026, 9, 1), today), DashboardAppService.ResolvePeriod(new DateOnly(2026, 9, 1), null, today));
        Assert.Equal((new DateOnly(2026, 7, 3), new DateOnly(2026, 8, 1)), DashboardAppService.ResolvePeriod(null, new DateOnly(2026, 8, 1), today));
        Assert.Equal((new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 5)), DashboardAppService.ResolvePeriod(new DateOnly(2026, 8, 5), new DateOnly(2026, 8, 1), today));
    }
}
