// TigerCS.Web is referenced under an alias - see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Application.Modules.Ticketing.Services;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Infrastructure.Modules.IdentityAndAccess.Repositories;
using TigerCS.Infrastructure.Modules.Ticketing.Repositories;
using TigerCS.Tests.Ticketing.Dashboard;
using TigerCS.Tests.Ticketing.Fakes;
using TigerCsWeb::TigerCS.Web.Models;

namespace TigerCS.Tests.Ticketing;

/// <summary>"AI (virtual agent)" display rule, Assigned To wording, and owner full-name resolution (incl. after the historical Closed-ticket backfill).</summary>
public sealed class AssignedToDisplayAndOwnerNameTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    // ---------------- AI (virtual agent) rule ----------------

    private static UnassignedTicketEvidence Evidence(
        TicketStatus status = TicketStatus.Open, Guid? owner = null, bool live = true, TimeSpan? silent = null, bool humanNeeded = false, bool phone = true) =>
        new(1, "TG-1", status, owner, 1, 1, Now.AddDays(-2), PendingApproval: false, HasGenesysInteraction: true, HasLiveGenesysInteraction: live,
            RequiresHumanFollowUp: humanNeeded, OpenHandoff: false, HumanEvidence: false, AnsweringEmployeeId: null, AnsweringGenesysUserId: null,
            PhonePickupByUnmappedAgent: false, UnmappedGenesysAgentId: null, LastActivityUtc: Now - (silent ?? TimeSpan.FromMinutes(5)),
            LeftCallCenter: false, ReleasedByPerson: false, LiveGenesysIsPhoneOnly: phone);

    [Theory]
    [InlineData(TicketStatus.Open)]
    [InlineData(TicketStatus.InProgress)]
    public void AnUnownedLiveAiTicketWithRecentActivity_IsShownAsTheVirtualAgent(TicketStatus status) =>
        Assert.True(AiVirtualAgentRule.IsActive(Evidence(status), Now));

    [Fact]
    public void ExactlySixHoursOfSilence_StillCounts_ButMoreDoesNot()
    {
        Assert.True(AiVirtualAgentRule.IsActive(Evidence(silent: TimeSpan.FromHours(6)), Now));
        Assert.False(AiVirtualAgentRule.IsActive(Evidence(silent: TimeSpan.FromHours(6) + TimeSpan.FromSeconds(1)), Now));
    }

    [Fact]
    public void NotShown_WhenAnEmployeeOwnsIt_TheConversationEnded_OrAPersonIsNeeded()
    {
        Assert.False(AiVirtualAgentRule.IsActive(Evidence(owner: Guid.NewGuid()), Now));
        Assert.False(AiVirtualAgentRule.IsActive(Evidence(live: false), Now));
        Assert.False(AiVirtualAgentRule.IsActive(Evidence(humanNeeded: true), Now));
    }

    [Theory]
    [InlineData(TicketStatus.Resolved)]
    [InlineData(TicketStatus.Closed)]
    [InlineData(TicketStatus.PendingCustomer)]
    public void NeverShown_ForClosedResolvedOrPendingTickets(TicketStatus status) =>
        Assert.False(AiVirtualAgentRule.IsActive(Evidence(status), Now));

    [Fact]
    public void ThePhoneStaleRule_TakesOverAfterSixHours_AndNeverOnChat()
    {
        var options = new StalePhoneAssignmentOptions().ToBackfillOptions();
        var stalePhone = Evidence(silent: TimeSpan.FromHours(7), phone: true);
        var staleChat = Evidence(silent: TimeSpan.FromHours(7), phone: false);

        Assert.False(AiVirtualAgentRule.IsActive(stalePhone, Now));
        Assert.True(CsAgentBackfillClassifier.IsStalePhoneAi(stalePhone, Now, options));      // released for automatic assignment
        Assert.False(CsAgentBackfillClassifier.IsStalePhoneAi(staleChat, Now, options));      // Chat keeps its existing protection
        Assert.Equal(CsAgentBackfillClass.ActiveAiConversation, CsAgentBackfillClassifier.Classify(staleChat, Now, options));
        Assert.False(CsAgentBackfillClassifier.IsStalePhoneAi(Evidence(silent: TimeSpan.FromHours(5)), Now, options));
    }

    // ---------------- Assigned To wording ----------------

    [Fact]
    public void AssignedTo_ShowsTheVirtualAgent_ForAnActiveUnownedAiTicket_WithoutAnyEmployee()
    {
        Assert.Equal("AI (virtual agent)", TicketDisplay.AssignedToLabel(null, null, 1, "CS", isActive: true, isAiVirtualAgent: true));
        Assert.Equal(TicketDisplay.UnassignedCsAgentLabel, TicketDisplay.AssignedToLabel(null, null, 1, "CS", isActive: true, isAiVirtualAgent: false));
    }

    [Fact]
    public void AssignedTo_IsEmpty_ForAClosedTicket_EvenWithAnAiFlagOrALeftoverOwner()
    {
        Assert.Equal(string.Empty, TicketDisplay.AssignedToLabel(null, null, 1, "CS", isActive: false, isAiVirtualAgent: true, isClosed: true));
        Assert.Equal(string.Empty, TicketDisplay.AssignedToLabel(Guid.NewGuid(), "Amal Agent", 1, "CS", isActive: false, isClosed: true));
    }

    [Fact]
    public void AssignedTo_ShowsTheFullName_AndNeverAnIdFallback()
    {
        var owner = Guid.Parse("a09b4a26-0000-0000-0000-000000000000");
        Assert.Equal("Amal Agent", TicketDisplay.AssignedToLabel(owner, "Amal Agent", 1, "CS"));
        var unknown = TicketDisplay.AssignedToLabel(owner, null, 1, "CS");
        Assert.Equal(TicketDisplay.UnknownEmployeeLabel, unknown);
        Assert.DoesNotContain("a09b4a26", unknown);
        Assert.DoesNotContain("Employee #", unknown);
    }

    // ---------------- Owner full-name resolution (real SQLite, real EF repositories) ----------------

    private readonly DashboardSqliteFixture _db = new(enforceForeignKeys: false);
    public void Dispose() => _db.Dispose();

    private sealed class CapturingLogger : ILogger<TicketQueryAppService>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class CountingEmployees(IEmployeeRepositoryProbe inner) : TigerCS.Application.Modules.IdentityAndAccess.Abstractions.IEmployeeRepository
    {
        public int NameLookups { get; private set; }
        public Task<Employee?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.Real.GetByIdAsync(id, ct);
        public Task AddAsync(Employee e, CancellationToken ct = default) => inner.Real.AddAsync(e, ct);
        public Task<int> CountActiveInRoleAsync(string r, CancellationToken ct = default) => inner.Real.CountActiveInRoleAsync(r, ct);
        public Task<IReadOnlyList<Employee>> ListAsync(bool i, IReadOnlyCollection<Guid>? ids, CancellationToken ct = default) => inner.Real.ListAsync(i, ids, ct);
        public Task<bool> IsReferencedByHistoryAsync(Guid id, CancellationToken ct = default) => inner.Real.IsReferencedByHistoryAsync(id, ct);
        public Task<IReadOnlyDictionary<Guid, string>> ResolveDisplayNamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        {
            NameLookups++;
            return inner.Real.ResolveDisplayNamesAsync(ids, ct);
        }
    }

    private sealed record IEmployeeRepositoryProbe(TigerCS.Application.Modules.IdentityAndAccess.Abstractions.IEmployeeRepository Real);

    private TicketQueryAppService Query(TigerCS.Infrastructure.Persistence.TigerCsDbContext context, out CountingEmployees employees, out CapturingLogger logger)
    {
        var time = new DashboardSqliteFixture.FixedTimeProvider(DashboardSqliteFixture.Now);
        employees = new CountingEmployees(new IEmployeeRepositoryProbe(new EmployeeRepository(context)));
        logger = new CapturingLogger();
        return new TicketQueryAppService(
            new TicketRepository(context, ReopenPolicy.Default, time), new UserDepartmentAssignmentRepository(context), new FakeTicketResolutionRepository(),
            new FakeTicketStatusHistoryRepository(), ReopenPolicy.Default, time, employeeRepository: employees, logger: logger);
    }

    private static readonly string[] Manager = [Roles.CsManager];

    [Fact]
    public async Task TheQueue_ReturnsEachOwnersFullName_InOneBatchedLookup_ForOwnersOutsideTheTicketsDepartment()
    {
        using var context = _db.CreateContext();
        // Owners sit in another department than the tickets and there are many tickets: no per-ticket lookup, no department-roster dependency.
        for (var i = 0; i < 6; i++)
        {
            _db.AddTicket(context, _db.CollectionsId, status: TicketStatus.InProgress, owner: i % 2 == 0 ? _db.CsAgentId : _db.RegistrationEmployeeId);
        }

        var result = await Query(context, out var employees, out _).GetQueueAsync(Guid.NewGuid(), Manager, new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 100));

        Assert.Equal(6, result.Items.Count);
        Assert.All(result.Items, i => Assert.Contains(i.CurrentOwnerName, new[] { "Amal Agent", "Rana Registrar" }));
        Assert.Equal(1, employees.NameLookups);
        Assert.DoesNotContain(result.Items, i => i.CurrentOwnerName!.Contains("Employee #") || Guid.TryParse(i.CurrentOwnerName, out _));
    }

    [Fact]
    public async Task ABlankDisplayName_FallsBackToTheUserName_ThenTheEmail()
    {
        using var context = _db.CreateContext();
        var ticket = _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.InProgress, owner: _db.CsAgentId);
        context.Database.ExecuteSqlInterpolated($"UPDATE Employees SET DisplayName = '' WHERE EmployeeId = {_db.CsAgentId}");

        var names = await new EmployeeRepository(context).ResolveDisplayNamesAsync([_db.CsAgentId]);
        Assert.Equal($"user-{_db.CsAgentId:N}", names[_db.CsAgentId]);

        context.Database.ExecuteSqlInterpolated($"UPDATE AspNetUsers SET UserName = NULL WHERE Id = {_db.CsAgentId}");
        names = await new EmployeeRepository(context).ResolveDisplayNamesAsync([_db.CsAgentId]);
        Assert.Equal($"user-{_db.CsAgentId:N}@example.test", names[_db.CsAgentId]);
        Assert.NotNull(ticket);
    }

    [Fact]
    public async Task AnOwnerWithoutAnEmployeeRecord_IsReported_AndTheAssignmentIsLeftUntouched()
    {
        using var context = _db.CreateContext();
        var ghost = Guid.NewGuid();
        var ticket = _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.InProgress, owner: ghost);

        var result = await Query(context, out _, out var logger).GetQueueAsync(Guid.NewGuid(), Manager, new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 100));

        var item = Assert.Single(result.Items);
        Assert.Equal(ghost, item.CurrentOwnerEmployeeId);          // the assignment is still reported
        Assert.Null(item.CurrentOwnerName);                         // no invented name; the page shows the neutral label
        Assert.Contains(logger.Warnings, w => w.Contains(ghost.ToString()));
        using var verify = _db.CreateContext();
        Assert.Equal(ghost, verify.Tickets.Single(t => t.TicketId == ticket.TicketId).CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task AfterTheHistoricalClosedTicketBackfill_OpenOwnersKeepTheirNames_ClosedTicketsHaveNoOwner_AndHistoryIsIntact()
    {
        long open, closedOwned;
        using (var context = _db.CreateContext())
        {
            open = _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.InProgress, owner: _db.CsAgentId).TicketId;
            var closed = _db.AddTicket(context, _db.CustomerServiceId, status: TicketStatus.Closed, owner: _db.CsAgentId, closedAtUtc: Now.AddDays(-30));
            closedOwned = closed.TicketId;
            // A historical row as production holds it: Closed but still owned, with a current assignment row.
            context.Database.ExecuteSqlInterpolated($"UPDATE Tickets SET CurrentOwnerEmployeeId = {_db.CsAgentId} WHERE TicketId = {closedOwned}");
            context.TicketAssignments.Add(new TicketAssignment(closedOwned, _db.CsAgentId, _db.CustomerServiceId, Now.AddDays(-31), null));
            context.SaveChanges();

            // What Backfill_TigerCS_PROD_ClosedTickets_ClearOwner.sql does with @DryRun = 0 (owner cleared, current row ended, nothing deleted).
            context.Database.ExecuteSqlRaw("UPDATE Tickets SET CurrentOwnerEmployeeId = NULL WHERE TicketStatus = {0} AND CurrentOwnerEmployeeId IS NOT NULL", (byte)TicketStatus.Closed);
            context.Database.ExecuteSqlRaw("UPDATE TicketAssignments SET IsCurrent = 0 WHERE TicketId IN (SELECT TicketId FROM Tickets WHERE TicketStatus = {0}) AND IsCurrent = 1", (byte)TicketStatus.Closed);
        }

        using var read = _db.CreateContext();
        var result = await Query(read, out _, out var logger).GetQueueAsync(Guid.NewGuid(), Manager, new TicketListRequestDto(null, null, null, null, null, null, null, null, null, 1, 100));

        var openItem = result.Items.Single(i => i.TicketId == open);
        Assert.Equal(_db.CsAgentId, openItem.CurrentOwnerEmployeeId);
        Assert.Equal("Amal Agent", openItem.CurrentOwnerName);                      // names still resolve after the backfill
        var closedItem = result.Items.Single(i => i.TicketId == closedOwned);
        Assert.Null(closedItem.CurrentOwnerEmployeeId);
        Assert.Null(closedItem.CurrentOwnerName);
        Assert.Equal(string.Empty, TicketDisplay.AssignedToLabel(closedItem.CurrentOwnerEmployeeId, closedItem.CurrentOwnerName, 1, "CS", isActive: false, isClosed: true));
        Assert.Empty(logger.Warnings);                                               // no "owner without employee" noise
        Assert.Equal(1, read.TicketAssignments.Count(a => a.TicketId == closedOwned && !a.IsCurrent && a.AssignedEmployeeId == _db.CsAgentId));   // history kept
        Assert.Equal(_db.CsAgentId, read.Tickets.Single(t => t.TicketId == open).CurrentOwnerEmployeeId);                                           // other tickets untouched
    }
}
