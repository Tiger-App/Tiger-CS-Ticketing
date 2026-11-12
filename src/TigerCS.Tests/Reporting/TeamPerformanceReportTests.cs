using Microsoft.AspNetCore.Identity;
using TigerCS.Application.Modules.Reporting.Dto;
using TigerCS.Application.Modules.Reporting.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Identity;
using TigerCS.Infrastructure.Modules.Reporting.Repositories;
using TigerCS.Infrastructure.Modules.WorkflowConfiguration.Seed;
using TigerCS.Infrastructure.Persistence;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.Reporting;

/// <summary>
/// The Team Performance report end to end from the application service
/// down to real SQL (SQLite in-memory over the real EF Core schema, the
/// same fixture the Dashboard uses): who is listed (every active employee in
/// every department), each of the four metrics, the zero-activity inclusion,
/// the one-row-per-employee rule, breach attribution to the holder at
/// breach time, and that every records list matches the count it was opened
/// from. The SLA details (Breached / Met / Pending evaluations behind the
/// SLA Breaches count) are covered in <see cref="TeamPerformanceSlaDetailsTests"/>.
/// </summary>
public sealed class TeamPerformanceReportTests : IDisposable
{
    private static readonly DateTime Now = DashboardSqliteFixture.Now;

    /// <summary>The default period the service resolves at <see cref="Now"/>: the last 30 UTC days, today included.</summary>
    private static readonly DateTime PeriodStart = Now.Date.AddDays(-29);

    private readonly DashboardSqliteFixture _db = new();

    private readonly Guid _aminaId = Guid.NewGuid();   // CS Agent, Customer Service
    private readonly Guid _bilalId = Guid.NewGuid();   // CS Agent, Call Center (primary) + Customer Service
    private readonly Guid _danaId = Guid.NewGuid();    // CS Agent, Customer Service, no activity
    private readonly Guid _hadiId = Guid.NewGuid();    // CS Supervisor, Customer Service
    private readonly Guid _coraId = Guid.NewGuid();    // Collections Officer + CS Supervisor, Collections
    private readonly Guid _retiredId = Guid.NewGuid(); // Deactivated CS Agent — not listed
    private int _callCenterId;
    private int _legalId;      // active department nobody belongs to yet
    private int _archivedId;   // inactive department

    public TeamPerformanceReportTests()
    {
        using var context = _db.CreateContext();

        var callCenter = new Department("Call Center", WorkflowReferenceData.CallCenterCode);
        var legal = new Department("Legal", "LGL");
        var archived = new Department("Archived Unit", "ARC", isActive: false);
        context.Departments.AddRange(callCenter, legal, archived);
        context.SaveChanges();
        _callCenterId = callCenter.DepartmentId;
        _legalId = legal.DepartmentId;
        _archivedId = archived.DepartmentId;

        AddEmployee(context, _aminaId, "Amina Agent", [Roles.CsAgent], [(_db.CustomerServiceId, true)]);
        AddEmployee(context, _bilalId, "Bilal Caller", [Roles.CsAgent], [(_callCenterId, true), (_db.CustomerServiceId, false)]);
        AddEmployee(context, _danaId, "Dana Quiet", [Roles.CsAgent], [(_db.CustomerServiceId, true)]);
        AddEmployee(context, _hadiId, "Hadi Supervisor", [Roles.CsSupervisor], [(_db.CustomerServiceId, true)]);
        AddEmployee(context, _coraId, "Cora Collector", ["Collections Officer", Roles.CsSupervisor], [(_db.CollectionsId, true)]);
        AddEmployee(context, _retiredId, "Rami Retired", [Roles.CsAgent], [(_db.CustomerServiceId, true)], deactivated: true);
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------

    private TeamPerformanceAppService CreateService(TigerCsDbContext context) =>
        new(new TeamPerformanceQueryRepository(context), new DashboardSqliteFixture.FixedTimeProvider(Now));

    private async Task<TeamPerformanceReportDto> ReportAsync(TeamPerformanceRequestDto? request = null)
    {
        using var context = _db.CreateContext();
        return await CreateService(context).GetReportAsync(request ?? new TeamPerformanceRequestDto());
    }

    private async Task<TeamPerformanceRecordsResult> RecordsAsync(Guid employeeId, string metric)
    {
        using var context = _db.CreateContext();
        return await CreateService(context).GetRecordsAsync(new TeamPerformanceRecordsRequestDto(employeeId, metric));
    }

    private static TeamPerformanceRowDto Row(TeamPerformanceReportDto report, Guid employeeId) =>
        report.Rows.Single(r => r.EmployeeId == employeeId);

    /// <summary>An Identity user + Employee with real AspNetUserRoles rows and department memberships — the eligibility rule reads exactly these tables.</summary>
    private static void AddEmployee(
        TigerCsDbContext context, Guid employeeId, string displayName, string[] roles, (int DepartmentId, bool IsPrimary)[] departments, bool deactivated = false)
    {
        context.Users.Add(new ApplicationUser
        {
            Id = employeeId,
            UserName = $"user-{employeeId:N}",
            NormalizedUserName = $"USER-{employeeId:N}",
            Email = $"user-{employeeId:N}@example.test",
            SecurityStamp = Guid.NewGuid().ToString()
        });

        var employee = new Employee(employeeId, displayName, isGeynessStaff: false, Now.AddDays(-60));
        if (deactivated)
        {
            employee.Deactivate(Now.AddDays(-1));
        }

        context.Employees.Add(employee);
        context.SaveChanges();

        foreach (var roleName in roles)
        {
            var role = context.Roles.SingleOrDefault(r => r.Name == roleName);
            if (role is null)
            {
                role = new ApplicationRole(roleName, roleName) { NormalizedName = roleName.ToUpperInvariant() };
                context.Roles.Add(role);
                context.SaveChanges();
            }

            context.UserRoles.Add(new IdentityUserRole<Guid> { UserId = employeeId, RoleId = role.Id });
        }

        foreach (var (departmentId, isPrimary) in departments)
        {
            context.UserDepartmentAssignments.Add(new UserDepartmentAssignment(employeeId, departmentId, isPrimary, Now.AddDays(-50), null));
        }

        context.SaveChanges();
    }

    /// <summary>Appends an assignment the way the real services do: the prior current row is superseded (the filtered unique index allows one current row per ticket), never rewritten.</summary>
    private static void Assign(TigerCsDbContext context, Ticket ticket, Guid employeeId, DateTime atUtc)
    {
        foreach (var current in context.TicketAssignments.Where(a => a.TicketId == ticket.TicketId && a.IsCurrent))
        {
            current.MarkSuperseded();
        }

        context.TicketAssignments.Add(new TicketAssignment(ticket.TicketId, employeeId, ticket.CurrentDepartmentId, atUtc, null));
        context.SaveChanges();
    }

    private static void RecordBreach(TigerCsDbContext context, Ticket ticket, DateTime atUtc)
    {
        context.TicketStatusHistoryEntries.Add(new TicketStatusHistory(
            ticket.TicketId, TicketStatusDimension.SlaState, (byte)SlaState.Running, (byte)SlaState.Breached,
            actorEmployeeId: null, actorIsSystem: true, note: "Resolution SLA breached.", Guid.NewGuid(), atUtc));
        context.SaveChanges();
    }

    private static TicketAgentHandoff AddHandoff(TigerCsDbContext context, Ticket ticket, Guid assignedTo, DateTime assignedAtUtc, DateTime? completedAtUtc)
    {
        var interaction = TicketInteraction.CreateLocal(ticket.TicketId, WellKnownChannels.Phone, "+971500000001", assignedAtUtc);
        context.TicketInteractions.Add(interaction);
        context.SaveChanges();

        var handoff = new TicketAgentHandoff(
            ticket.TicketId, interaction.TicketInteractionId, ticket.CurrentDepartmentId, WellKnownChannels.Phone,
            requestedAtUtc: assignedAtUtc, createdAtUtc: assignedAtUtc,
            assignedEmployeeId: assignedTo, assignedAtUtc: assignedAtUtc);
        if (completedAtUtc is { } completed)
        {
            handoff.Complete(assignedTo, completed, "Done.");
        }

        context.TicketAgentHandoffs.Add(handoff);
        context.SaveChanges();
        return handoff;
    }

    // ---------------------------------------------------------------
    // Who is listed
    // ---------------------------------------------------------------

    [Fact]
    public async Task Rows_ListEveryActiveEmployeeInEveryDepartmentExactlyOnce_SortedByName()
    {
        var report = await ReportAsync();

        var names = report.Rows.Select(r => r.DisplayName).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(), names);
        Assert.Equal(report.Rows.Count, report.Rows.Select(r => r.EmployeeId).Distinct().Count());
        Assert.Equal(report.Rows.Count, report.Totals.Employees);

        // Customer Service, Call Center, Collections (by role-holders and by a role-less fixture employee),
        // Registration, the supervisor — all there. The deactivated agent is not.
        foreach (var name in new[] { "Amina Agent", "Bilal Caller", "Dana Quiet", "Hadi Supervisor", "Cora Collector", "Amal Agent", "Hadi Head", "Rana Registrar", "Walid Worker" })
        {
            Assert.Contains(name, names);
        }

        Assert.DoesNotContain("Rami Retired", names);

        var amina = Row(report, _aminaId);
        Assert.Equal([Roles.CsAgent], amina.Roles);
        Assert.Equal(["Customer Service"], amina.Departments.Select(d => d.Name).ToArray());

        // Two departments, ONE row — primary first.
        var bilal = Row(report, _bilalId);
        Assert.Equal(["Call Center", "Customer Service"], bilal.Departments.Select(d => d.Name).ToArray());
        Assert.True(bilal.Departments[0].IsPrimary);
        Assert.Equal(("Call Center", true), (bilal.PrimaryDepartment!.Name, bilal.PrimaryDepartment.IsPrimary));
        Assert.Equal("Customer Service", amina.PrimaryDepartment!.Name);
        Assert.Single(report.Rows, r => r.EmployeeId == _bilalId);

        // A Collections employee with two roles shows both, alphabetically, and their department.
        var cora = Row(report, _coraId);
        Assert.Equal(["Collections Officer", Roles.CsSupervisor], cora.Roles);
        Assert.Equal(["Collections"], cora.Departments.Select(d => d.Name).ToArray());

        // A role-less fixture employee in Registration is still a row.
        var rana = report.Rows.Single(r => r.DisplayName == "Rana Registrar");
        Assert.Empty(rana.Roles);
        Assert.Equal(["Registration"], rana.Departments.Select(d => d.Name).ToArray());

        var dana = Row(report, _danaId);
        Assert.Equal((0, 0, 0, 0), (dana.CurrentlyAssigned, dana.TicketsWorked, dana.CompletedFollowUps, dana.TicketsWithSlaBreach));
    }

    private Guid AddExtraEmployee(string name, params (int DepartmentId, bool IsPrimary)[] departments)
    {
        var id = Guid.NewGuid();
        using var context = _db.CreateContext();
        AddEmployee(context, id, name, [], departments);
        return id;
    }

    [Fact]
    public async Task PrimaryDepartment_EmployeeWithSeveralDepartmentPermissions_ShowsOnlyThePrimary_AndIsListedOnceUnderIt()
    {
        // Access to three departments; the primary is Legal, deliberately not the alphabetically/first-listed one.
        var multi = AddExtraEmployee("Zed Multi", (_db.CustomerServiceId, false), (_db.CollectionsId, false), (_legalId, true));

        var all = await ReportAsync();
        Assert.Single(all.Rows, r => r.EmployeeId == multi);
        var row = Row(all, multi);
        Assert.Equal(_legalId, row.PrimaryDepartment!.DepartmentId);
        Assert.Equal("Legal", row.PrimaryDepartment.Name);
        Assert.Equal(3, row.Departments.Count); // access/secondary departments are unchanged

        // Only the primary department's filter finds them.
        Assert.Contains(multi, (await ReportAsync(new TeamPerformanceRequestDto(DepartmentId: _legalId))).Rows.Select(r => r.EmployeeId));
        Assert.DoesNotContain(multi, (await ReportAsync(new TeamPerformanceRequestDto(DepartmentId: _db.CustomerServiceId))).Rows.Select(r => r.EmployeeId));
        Assert.DoesNotContain(multi, (await ReportAsync(new TeamPerformanceRequestDto(DepartmentId: _db.CollectionsId))).Rows.Select(r => r.EmployeeId));

        // Every employee is in exactly one department filter (or none, when they have no primary).
        var departmentIds = all.FilterOptions.Departments.Select(d => int.Parse(d.Id)).ToList();
        var seen = new List<Guid>();
        foreach (var id in departmentIds)
        {
            seen.AddRange((await ReportAsync(new TeamPerformanceRequestDto(DepartmentId: id))).Rows.Select(r => r.EmployeeId));
        }

        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(all.Rows.Count(r => r.PrimaryDepartment is not null), seen.Count);
    }

    [Fact]
    public async Task PrimaryDepartment_EmployeeWithoutOne_HasNullPrimary_NoFallbackToAnAccessDepartment_AndMatchesNoDepartmentFilter()
    {
        var none = AddExtraEmployee("Nora NoPrimary", (_db.CustomerServiceId, false), (_legalId, false));
        var noMembership = AddExtraEmployee("Omar Orphan");

        var all = await ReportAsync();
        foreach (var id in new[] { none, noMembership })
        {
            Assert.Single(all.Rows, r => r.EmployeeId == id);
            Assert.Null(Row(all, id).PrimaryDepartment);
        }

        Assert.Equal(2, Row(all, none).Departments.Count); // access kept, but none of it is "primary"

        foreach (var option in all.FilterOptions.Departments)
        {
            var rows = (await ReportAsync(new TeamPerformanceRequestDto(DepartmentId: int.Parse(option.Id)))).Rows.Select(r => r.EmployeeId).ToList();
            Assert.DoesNotContain(none, rows);
            Assert.DoesNotContain(noMembership, rows);
        }

        // The employee's own drill-down still resolves with no department filter.
        using var context = _db.CreateContext();
        var records = await CreateService(context).GetRecordsAsync(new TeamPerformanceRecordsRequestDto(none, TeamPerformanceMetric.CurrentlyAssigned.ToString()));
        Assert.Equal(TeamPerformanceRecordsOutcome.Success, records.Outcome);
    }

    [Fact]
    public async Task FilterOptions_IncludeADepartmentThatIsAnActiveEmployeesPrimary_EvenWhenTheDepartmentIsInactive()
    {
        AddExtraEmployee("Ida Inactive", (_archivedId, true));

        var report = await ReportAsync();
        Assert.Contains(report.FilterOptions.Departments, d => d.Id == _archivedId.ToString());
        var byArchived = await ReportAsync(new TeamPerformanceRequestDto(DepartmentId: _archivedId));
        Assert.Equal(["Ida Inactive"], byArchived.Rows.Select(r => r.DisplayName).ToArray());
    }

    [Fact]
    public async Task FilterOptions_OfferEveryConfiguredDepartmentEveryEmployeeAndEveryHeldRole()
    {
        var report = await ReportAsync();

        // Every ACTIVE configured department — including Legal, which has no members — and not the inactive one.
        var departments = report.FilterOptions.Departments.Select(d => d.Label).ToArray();
        Assert.Equal(["Call Center", "Collections", "Customer Service", "Legal", "Registration"], departments);
        Assert.Contains(report.FilterOptions.Departments, d => d.Id == _legalId.ToString());
        Assert.DoesNotContain(report.FilterOptions.Departments, d => d.Id == _archivedId.ToString());

        Assert.Equal(report.Rows.Count, report.FilterOptions.Employees.Count);
        Assert.Contains(report.FilterOptions.Employees, e => e.Id == _coraId.ToString() && e.GroupLabel == "Collections Officer, CS Supervisor");

        Assert.Equal(["Collections Officer", Roles.CsAgent, Roles.CsSupervisor], report.FilterOptions.Roles);
    }

    [Fact]
    public void CallCenterDepartmentCode_IsTheSameCodeTheReferenceDataSeeds()
    {
        // The routing and Genesys services reference this constant; if the
        // seed ever changes they must change with it.
        Assert.Equal(WorkflowReferenceData.CallCenterCode, TeamPerformanceAppService.CallCenterDepartmentCode);
    }

    // ---------------------------------------------------------------
    // Metrics
    // ---------------------------------------------------------------

    [Fact]
    public async Task CurrentlyAssigned_CountsOpenTicketsHeldNow_NotClosedOnes_RegardlessOfPeriod()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CustomerServiceId, owner: _aminaId);
            _db.AddTicket(context, _db.CustomerServiceId, owner: _aminaId, status: TicketStatus.InProgress);
            _db.AddTicket(context, _db.CustomerServiceId, owner: _aminaId, status: TicketStatus.Resolved);
            _db.AddTicket(context, _db.CustomerServiceId, owner: _aminaId, status: TicketStatus.Closed);
            // Old, but still held — the period does not apply to current state.
            _db.AddTicket(context, _db.CustomerServiceId, owner: _aminaId, createdAtUtc: Now.AddDays(-200));
            _db.AddTicket(context, _db.CustomerServiceId, owner: _bilalId);
            _db.AddTicket(context, _db.CustomerServiceId);
        }

        var report = await ReportAsync();

        Assert.Equal(4, Row(report, _aminaId).CurrentlyAssigned);
        Assert.Equal(1, Row(report, _bilalId).CurrentlyAssigned);
        Assert.Equal(5, report.Totals.CurrentlyAssigned);
    }

    [Fact]
    public async Task TicketsWorked_CountsDistinctTicketsAcrossEveryActivitySource_WithinThePeriodOnly()
    {
        using (var context = _db.CreateContext())
        {
            var t1 = _db.AddTicket(context, _db.CustomerServiceId);
            var t2 = _db.AddTicket(context, _db.CustomerServiceId);
            var t3 = _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.Resolved);
            var t4 = _db.AddTicket(context, _db.CustomerServiceId);
            var t5 = _db.AddTicket(context, _db.CustomerServiceId);
            var t6 = _db.AddTicket(context, _db.CustomerServiceId);
            var t7 = _db.AddTicket(context, _db.CustomerServiceId, owner: _aminaId);

            // t1: assigned AND noted by Amina — one ticket, not two.
            Assign(context, t1, _aminaId, Now.AddDays(-3));
            context.TicketNotes.Add(new TicketNote(t1.TicketId, "Called the customer.", _aminaId, Now.AddDays(-2)));
            // t2: a status change by Amina.
            context.TicketStatusHistoryEntries.Add(new TicketStatusHistory(
                t2.TicketId, TicketStatusDimension.TicketStatus, (byte)TicketStatus.Open, (byte)TicketStatus.InProgress,
                _aminaId, false, null, Guid.NewGuid(), Now.AddDays(-4)));
            // t3: a resolution by Amina.
            context.TicketResolutions.Add(new TicketResolution(t3.TicketId, ResolutionOutcome.Resolved, "Fixed.", null, null, _aminaId, Now.AddDays(-5)));
            // t4: a workflow event by Amina.
            context.TicketWorkflowEvents.Add(new TicketWorkflowEvent(t4.TicketId, WorkflowEventType.PrerequisitesCompleted, Now.AddDays(-6), _aminaId, null, null, Guid.NewGuid()));
            context.SaveChanges();
            // t5: a follow-up assigned to Amina (not completed).
            AddHandoff(context, t5, _aminaId, Now.AddDays(-7), null);
            // t6: an assignment to Amina BEFORE the period — not worked in it.
            Assign(context, t6, _aminaId, PeriodStart.AddDays(-1));
            // t7: Amina holds it now but nothing in history says she touched
            // it in the period — current ownership is not work performed.
            // Bilal: one note.
            context.TicketNotes.Add(new TicketNote(t2.TicketId, "Checked.", _bilalId, Now.AddDays(-1)));
            context.SaveChanges();
        }

        var report = await ReportAsync();

        Assert.Equal(5, Row(report, _aminaId).TicketsWorked);
        Assert.Equal(1, Row(report, _bilalId).TicketsWorked);
        Assert.Equal(0, Row(report, _danaId).TicketsWorked);
        // t2 was worked by both — the team total counts the effort, so 6.
        Assert.Equal(6, report.Totals.TicketsWorked);
    }

    [Fact]
    public async Task CompletedFollowUps_CountsHandoffsCompletedInThePeriod_PerWorkItem()
    {
        using (var context = _db.CreateContext())
        {
            var t1 = _db.AddTicket(context, _db.CustomerServiceId);
            var t2 = _db.AddTicket(context, _db.CustomerServiceId);
            AddHandoff(context, t1, _aminaId, Now.AddDays(-3), Now.AddDays(-2));
            AddHandoff(context, t1, _aminaId, Now.AddDays(-2), Now.AddDays(-1)); // same ticket, second work item
            AddHandoff(context, t2, _aminaId, Now.AddDays(-3), null);           // still open
            AddHandoff(context, t2, _aminaId, PeriodStart.AddDays(-5), PeriodStart.AddDays(-4)); // before the period
            AddHandoff(context, t2, _bilalId, Now.AddDays(-1), Now.AddHours(-1));
        }

        var report = await ReportAsync();

        Assert.Equal(2, Row(report, _aminaId).CompletedFollowUps);
        Assert.Equal(1, Row(report, _bilalId).CompletedFollowUps);
        Assert.Equal(3, report.Totals.CompletedFollowUps);
    }

    [Fact]
    public async Task SlaBreaches_AreAttributedToWhoeverHeldTheTicketWhenItBreached_NotTheCurrentOwner()
    {
        using (var context = _db.CreateContext())
        {
            // Amina held it when it breached; Bilal holds it now.
            var reassigned = _db.AddTicket(context, _db.CustomerServiceId, owner: _bilalId, slaBreached: true);
            Assign(context, reassigned, _aminaId, Now.AddDays(-10));
            RecordBreach(context, reassigned, Now.AddDays(-5));
            Assign(context, reassigned, _bilalId, Now.AddDays(-2));

            // Two breach rows (first response, then resolution) on one ticket
            // held by Amina throughout — one ticket, counted once.
            var twice = _db.AddTicket(context, _db.CustomerServiceId, owner: _aminaId, slaBreached: true);
            Assign(context, twice, _aminaId, Now.AddDays(-8));
            RecordBreach(context, twice, Now.AddDays(-6));
            RecordBreach(context, twice, Now.AddDays(-3));

            // Breached before the period — not counted.
            var old = _db.AddTicket(context, _db.CustomerServiceId, owner: _aminaId, slaBreached: true, createdAtUtc: Now.AddDays(-60));
            Assign(context, old, _aminaId, Now.AddDays(-59));
            RecordBreach(context, old, PeriodStart.AddDays(-1));

            // Breached while nobody held it, assigned to Dana afterwards — attributed to nobody.
            var queued = _db.AddTicket(context, _db.CustomerServiceId, owner: _danaId, slaBreached: true);
            RecordBreach(context, queued, Now.AddDays(-4));
            Assign(context, queued, _danaId, Now.AddDays(-1));
        }

        var report = await ReportAsync();

        Assert.Equal(2, Row(report, _aminaId).TicketsWithSlaBreach);
        Assert.Equal(3, Row(report, _aminaId).SlaBreachEvents);   // the ticket with two breach rows is two events but one ticket
        Assert.Equal(0, Row(report, _bilalId).TicketsWithSlaBreach);
        Assert.Equal(0, Row(report, _danaId).TicketsWithSlaBreach);
        Assert.Equal(2, report.Totals.TicketsWithSlaBreach);
    }

    // ---------------------------------------------------------------
    // Filters
    // ---------------------------------------------------------------

    [Fact]
    public async Task Filters_NarrowTheRows_WhileThePickersStillOfferEveryone()
    {
        var allCount = (await ReportAsync()).Rows.Count;

        var byRole = await ReportAsync(new TeamPerformanceRequestDto(Role: "cs agent"));
        Assert.Equal([_aminaId, _bilalId, _danaId], byRole.Rows.Select(r => r.EmployeeId).ToArray());
        Assert.Equal(Roles.CsAgent, byRole.Filters.Role);
        Assert.Equal(allCount, byRole.FilterOptions.Employees.Count);

        // A multi-role employee is found by either of their roles.
        Assert.Equal([_coraId], (await ReportAsync(new TeamPerformanceRequestDto(Role: "Collections Officer"))).Rows.Select(r => r.EmployeeId).ToArray());
        Assert.Contains(_coraId, (await ReportAsync(new TeamPerformanceRequestDto(Role: Roles.CsSupervisor))).Rows.Select(r => r.EmployeeId));

        // The Department filter uses the PRIMARY department only: Bilal has access to Customer Service but his primary is Call Center.
        var byDepartment = await ReportAsync(new TeamPerformanceRequestDto(DepartmentId: _db.CustomerServiceId));
        Assert.DoesNotContain(_bilalId, byDepartment.Rows.Select(r => r.EmployeeId));
        Assert.Contains(_aminaId, byDepartment.Rows.Select(r => r.EmployeeId));
        Assert.DoesNotContain(_coraId, byDepartment.Rows.Select(r => r.EmployeeId));
        var byCallCenter = await ReportAsync(new TeamPerformanceRequestDto(DepartmentId: _callCenterId));
        Assert.Equal([_bilalId], byCallCenter.Rows.Select(r => r.EmployeeId).ToArray());

        // Collections employees appear, and department + role combine.
        var byCollections = await ReportAsync(new TeamPerformanceRequestDto(DepartmentId: _db.CollectionsId));
        Assert.Equal(["Cora Collector", "Hadi Head"], byCollections.Rows.Select(r => r.DisplayName).ToArray());
        var collectionsOfficers = await ReportAsync(new TeamPerformanceRequestDto(Role: "Collections Officer", DepartmentId: _db.CollectionsId));
        Assert.Equal([_coraId], collectionsOfficers.Rows.Select(r => r.EmployeeId).ToArray());
        Assert.Empty((await ReportAsync(new TeamPerformanceRequestDto(Role: "Collections Officer", DepartmentId: _callCenterId))).Rows);

        // A configured department nobody belongs to filters to an empty report, not an error.
        var empty = await ReportAsync(new TeamPerformanceRequestDto(DepartmentId: _legalId));
        Assert.Empty(empty.Rows);
        Assert.Equal(0, empty.Totals.Employees);

        var byEmployee = await ReportAsync(new TeamPerformanceRequestDto(EmployeeId: _danaId));
        Assert.Equal([_danaId], byEmployee.Rows.Select(r => r.EmployeeId).ToArray());

        // A supervisor is on the report now; a deactivated employee still yields no rows, never an error.
        Assert.Single((await ReportAsync(new TeamPerformanceRequestDto(EmployeeId: _hadiId))).Rows);
        Assert.Empty((await ReportAsync(new TeamPerformanceRequestDto(EmployeeId: _retiredId))).Rows);

        // An unknown role is ignored rather than refused.
        var unknownRole = await ReportAsync(new TeamPerformanceRequestDto(Role: "Wizard"));
        Assert.Equal(allCount, unknownRole.Rows.Count);
        Assert.Null(unknownRole.Filters.Role);
    }

    [Fact]
    public async Task Totals_AreTheSumOfTheFilteredEmployeesOnly()
    {
        using (var context = _db.CreateContext())
        {
            var collections = _db.AddTicket(context, _db.CollectionsId, owner: _coraId);
            Assign(context, collections, _coraId, Now.AddDays(-2));
            _db.AddTicket(context, _db.CustomerServiceId, owner: _aminaId);
        }

        var all = await ReportAsync();
        var collectionsOnly = await ReportAsync(new TeamPerformanceRequestDto(DepartmentId: _db.CollectionsId));

        Assert.Equal(2, all.Totals.CurrentlyAssigned);
        Assert.Equal(1, collectionsOnly.Totals.CurrentlyAssigned);
        Assert.Equal(1, collectionsOnly.Totals.TicketsWorked);
        Assert.Equal(collectionsOnly.Rows.Sum(r => r.TicketsWithSlaBreach), collectionsOnly.Totals.TicketsWithSlaBreach);
        Assert.Equal(collectionsOnly.Rows.Count, collectionsOnly.Totals.Employees);
    }

    [Fact]
    public async Task Period_DefaultsToTheLastThirtyDays_AndAnExplicitRangeBoundsTheActivity()
    {
        using (var context = _db.CreateContext())
        {
            var t1 = _db.AddTicket(context, _db.CustomerServiceId);
            var t2 = _db.AddTicket(context, _db.CustomerServiceId);
            Assign(context, t1, _aminaId, Now.AddDays(-20));
            Assign(context, t2, _aminaId, Now.AddDays(-2));
        }

        var defaulted = await ReportAsync();
        Assert.Equal(DateOnly.FromDateTime(PeriodStart), defaulted.Filters.DateFrom);
        Assert.Equal(DateOnly.FromDateTime(Now), defaulted.Filters.DateTo);
        Assert.Equal(2, Row(defaulted, _aminaId).TicketsWorked);

        var lastWeek = await ReportAsync(new TeamPerformanceRequestDto(
            DateFrom: DateOnly.FromDateTime(Now.AddDays(-7)), DateTo: DateOnly.FromDateTime(Now)));
        Assert.Equal(1, Row(lastWeek, _aminaId).TicketsWorked);
    }

    // ---------------------------------------------------------------
    // Records behind a count
    // ---------------------------------------------------------------

    [Fact]
    public async Task Records_MatchTheCountTheyWereOpenedFrom_AndCarryTheMetricSpecificInstant()
    {
        long breachedTicketId;
        using (var context = _db.CreateContext())
        {
            var held = _db.AddTicket(context, _db.CustomerServiceId, owner: _aminaId);
            var breached = _db.AddTicket(context, _db.CustomerServiceId, owner: _bilalId, slaBreached: true);
            breachedTicketId = breached.TicketId;
            Assign(context, breached, _aminaId, Now.AddDays(-10));
            RecordBreach(context, breached, Now.AddDays(-5));
            Assign(context, breached, _bilalId, Now.AddDays(-2));
            AddHandoff(context, held, _aminaId, Now.AddDays(-3), Now.AddDays(-1));
        }

        var report = await ReportAsync();
        var amina = Row(report, _aminaId);

        foreach (var metric in Enum.GetValues<TeamPerformanceMetric>())
        {
            var result = await RecordsAsync(_aminaId, metric.ToString());
            Assert.Equal(TeamPerformanceRecordsOutcome.Success, result.Outcome);
            var expected = metric switch
            {
                TeamPerformanceMetric.CurrentlyAssigned => amina.CurrentlyAssigned,
                TeamPerformanceMetric.TicketsWorked => amina.TicketsWorked,
                TeamPerformanceMetric.CompletedFollowUps => amina.CompletedFollowUps,
                _ => amina.TicketsWithSlaBreach
            };
            Assert.Equal(expected, result.Records!.Records.Count);
            Assert.Equal("Amina Agent", result.Records.DisplayName);
            Assert.Equal(metric.ToString(), result.Records.Metric);
            Assert.All(result.Records.Records, r => Assert.False(string.IsNullOrWhiteSpace(r.TicketNumber)));
        }

        var breaches = (await RecordsAsync(_aminaId, "slaBreaches")).Records!;
        var breach = Assert.Single(breaches.Records);
        Assert.Equal(breachedTicketId, breach.TicketId);
        Assert.Equal(Now.AddDays(-5), breach.BreachedAtUtc);
        Assert.Equal("Customer Service", breach.DepartmentName);
        Assert.Null(breach.CompletedAtUtc);

        var followUps = (await RecordsAsync(_aminaId, nameof(TeamPerformanceMetric.CompletedFollowUps))).Records!;
        var followUp = Assert.Single(followUps.Records);
        Assert.Equal(Now.AddDays(-1), followUp.CompletedAtUtc);
        Assert.Null(followUp.BreachedAtUtc);

        // Bilal holds the breached ticket now, so it is in his Currently
        // Assigned list — and not in his SLA Breaches, which are Amina's.
        Assert.Contains((await RecordsAsync(_bilalId, nameof(TeamPerformanceMetric.CurrentlyAssigned))).Records!.Records, r => r.TicketId == breachedTicketId);
        Assert.Empty((await RecordsAsync(_bilalId, nameof(TeamPerformanceMetric.SlaBreaches))).Records!.Records);
    }

    [Fact]
    public async Task Records_RefuseAnUnknownMetric_AndAnEmployeeWhoIsNotOnTheReport()
    {
        Assert.Equal(TeamPerformanceRecordsOutcome.UnknownMetric, (await RecordsAsync(_aminaId, "Velocity")).Outcome);
        Assert.Equal(TeamPerformanceRecordsOutcome.Success, (await RecordsAsync(_hadiId, "TicketsWorked")).Outcome);
        Assert.Equal(TeamPerformanceRecordsOutcome.Success, (await RecordsAsync(_coraId, "TicketsWorked")).Outcome);
        Assert.Equal(TeamPerformanceRecordsOutcome.EmployeeNotEligible, (await RecordsAsync(_retiredId, "TicketsWorked")).Outcome);
        Assert.Equal(TeamPerformanceRecordsOutcome.EmployeeNotEligible, (await RecordsAsync(Guid.NewGuid(), "TicketsWorked")).Outcome);
    }
}
