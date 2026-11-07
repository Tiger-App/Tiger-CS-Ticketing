using Microsoft.Extensions.Logging.Abstractions;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Application.Modules.Collections.Reconciliation;
using TigerCS.Application.Modules.Collections.Services;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Tests.Collections.Snapshot;
using TigerCS.Tests.IdentityAndAccess.Fakes;

namespace TigerCS.Tests.Collections.Reconciliation;

/// <summary>Reconciliation runs, the single source gate and the evidence it relies on: missing evidence is not a pass, a difference is not an error, and nothing starts by itself.</summary>
public sealed class FinancialReconciliationServiceTests
{
    private static readonly DateTime Now = new(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc);
    private static readonly CollectionsCaller Manager = new(Guid.NewGuid(), [Roles.CsManager], []);

    private sealed class FakeDirect : IReconciliationSource
    {
        public List<PactReceivableInstalment> Rows { get; } = [];
        public Exception? Throw { get; set; }
        public int Reads { get; private set; }
        /// <summary>The window the last read was asked for: the proof that PACT is read for the configured range, not only that the range is displayed.</summary>
        public (DateOnly From, DateOnly Through)? LastWindow { get; private set; }
        public string ProcedureLabel(int companyId) => $"dbo.p{companyId}AccountReceivables";
        public Task<IReadOnlyList<PactReceivableInstalment>> ReadAsync(int companyId, DateOnly from, DateOnly through, CancellationToken ct)
        {
            Reads++; LastWindow = (from, through);
            if (Throw is not null) throw Throw;
            return Task.FromResult<IReadOnlyList<PactReceivableInstalment>>(Rows.Where(r => r.CompanyId == companyId && DateOnly.FromDateTime(r.DueDate) >= from && DateOnly.FromDateTime(r.DueDate) <= through).ToList());
        }
    }

    /// <summary>The per-company snapshot refresh as the reconciliation uses it: copies PACT into the snapshot (Sync), publishes without changing anything (NoChange: a mapping defect), fails, or is busy.</summary>
    private sealed class FakeRefresher(Harness h) : IReceivablesRefresher
    {
        public string Mode { get; set; } = "Sync";
        public List<(string Trigger, int? Company)> Calls { get; } = [];
        public Task<ReceivablesRefreshResult> RefreshAsync(string triggerSource, int? companyId, CancellationToken ct, DateOnly? fromDate = null, DateOnly? throughDate = null)
        {
            Calls.Add((triggerSource, companyId));
            var company = companyId!.Value;
            switch (Mode)
            {
                case "Fail":
                    return Task.FromResult(new ReceivablesRefreshResult(Guid.NewGuid(), "Failed", $"Company {company}: PACT timed out", [new(company, "Failed", null, null, null, null, null, -2, "PACT timed out")]));
                case "AlreadyRunning":
                    return Task.FromResult(new ReceivablesRefreshResult(null, "AlreadyRunning", "busy", [new(company, "AlreadyRunning", null, null, null, null, null, null, "busy")]));
                case "Sync":
                    h.Snapshot.Rows.RemoveAll(r => r.CompanyId == company);
                    h.Snapshot.Rows.AddRange(h.Direct.Rows.Where(r => r.CompanyId == company));
                    break;
            }
            h.Clock = h.Clock.AddMinutes(1);
            h.Snapshot.Companies[company] = FakeSnapshotSource.Healthy(company, h.Clock);     // a new publish
            return Task.FromResult(new ReceivablesRefreshResult(Guid.NewGuid(), "Succeeded", null, [new(company, "Succeeded", 10, 10, 0, 0, 0, null, null)]));
        }
    }

    private sealed class SyncLauncher : IReconciliationLauncher
    {
        public FinancialReconciliationService? Service { get; set; }
        public int Launches { get; private set; }
        public void Launch(Guid runId) { Launches++; Service!.ExecuteAsync(runId, CancellationToken.None).GetAwaiter().GetResult(); }
    }

    private sealed class Harness
    {
        public FakeSnapshotSource Snapshot = new(Now);
        public FakeDirect Direct = new();
        public ReconciliationHistory History = new();
        public SyncLauncher Launcher = new();
        public CollectionsCampaignOptions Campaign = new() { FinancialSourceValidated = true };
        public PactReceivablesOptions Source = new() { Enabled = true };
        public ReceivablesSnapshotOptions SnapshotOptions = new();
        public FinancialReconciliationService Service;
        public DateTime Clock = Now;
        public FakeRefresher? Refresher;

        public Harness(bool launcher = true, string? refresher = null)
        {
            var options = new CollectionsOptions { Enabled = true };
            var time = new FixedTime(this);
            if (refresher is not null) Refresher = new FakeRefresher(this) { Mode = refresher };
            Service = new(options, Campaign, Source, new CollectionsAuthorizationService(options, new FakeDepartmentRepository()), new CollectionsClock(options, time), Snapshot, Direct, History,
                NullLogger<FinancialReconciliationService>.Instance, launcher ? Launcher : null, SnapshotOptions, Refresher);
            Launcher.Service = Service;
            Snapshot.MaxAgeMinutes = 180;
            Snapshot.NowUtc = () => Clock;
        }

        public static PactReceivableInstalment Row(int company, string unit, string voucher, DateTime due, decimal amount, int unitId = 11) =>
            new(company, "T" + unitId, "Name", "0501234567", "a@b.ae", unitId, unit, "140", voucher, "", due, amount, "Unpaid");

        public void Both(PactReceivableInstalment row) { Snapshot.Rows.Add(row); Direct.Rows.Add(row); }
        private sealed class FixedTime(Harness h) : TimeProvider { public override DateTimeOffset GetUtcNow() => new(h.Clock, TimeSpan.Zero); }
    }

    private static ReconciliationRunDto Latest(Harness h) => h.History.Latest()!;

    // ------------------------------------------------------------ the window: due dates from ReconciliationStartDate (2014-01-01) to the end of the current month

    [Fact]
    public async Task TheWindow_StartsAt2014ByDefault_EndsAtTheMonthEnd_AndBothSidesAreReadAndComparedForIt()
    {
        var h = new Harness();
        Assert.Equal(new DateTime(2014, 1, 1), h.Campaign.ReconciliationStartDate);
        Assert.Equal((new DateOnly(2014, 1, 1), new DateOnly(2026, 10, 31)), h.Service.Window());                  // the clock is 22 Oct 2026
        // An old instalment that exists only in PACT is found: the direct read and the snapshot read are both asked for the window from 2014, and the comparison covers it.
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        h.Direct.Rows.Add(Harness.Row(4, "TP140-102", "OLD-2015", new(2015, 3, 1), 250m, 12));
        h.Both(Harness.Row(32, "TP127-201", "V2", new(2026, 10, 5), 500m, 21));
        var started = await h.Service.StartAsync(Manager, null, CancellationToken.None);
        Assert.True(started.IsSuccess, started.Detail);
        var run = Latest(h);
        Assert.Equal((new DateOnly(2014, 1, 1), new DateOnly(2026, 10, 31)), (run.From, run.Through));
        Assert.Equal((new DateOnly(2014, 1, 1), new DateOnly(2026, 10, 31)), h.Direct.LastWindow);
        Assert.Equal(new DateOnly(2014, 1, 1), h.Snapshot.LastRequest!.FromDate); Assert.Equal(new DateOnly(2026, 10, 31), h.Snapshot.LastRequest.ThroughDate);
        Assert.Equal(ReconciliationStates.Failed, run.Status);
        var dubai = run.Companies.Single(c => c.CompanyId == 4);
        Assert.Equal((ReconciliationStates.Failed, 1), (dubai.Status, dubai.Comparison!.UnitsOnlyInSource));                 // the 2015 row is compared, not skipped
        Assert.Equal(ReconciliationStates.Passed, run.Companies.Single(c => c.CompanyId == 32).Status);
        Assert.Equal(0.01m, run.Tolerance);
    }

    [Fact]
    public async Task AnInstalmentOlderThanTheStartDate_IsOutsideTheWindow_OnBothSides()
    {
        var h = new Harness();
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        h.Direct.Rows.Add(Harness.Row(4, "TP140-102", "OLD-2013", new(2013, 12, 31), 250m, 12));                              // the day before the window
        h.Both(Harness.Row(32, "TP127-201", "V2", new(2026, 10, 5), 500m, 21));
        Assert.True((await h.Service.StartAsync(Manager, null, CancellationToken.None)).IsSuccess);
        Assert.Equal(ReconciliationStates.Passed, Latest(h).Status);
    }

    [Fact]
    public async Task TheReconciliationWindow_IsItsOwn_NotTheCampaignsScope_AndNeverBeforeTheSnapshotCoverage()
    {
        var h = new Harness();
        h.Source.StartDate = new DateTime(2026, 1, 1);                                                                        // the Campaigns eligibility scope: unchanged and not used here
        h.Campaign.ReconciliationStartDate = new DateTime(2018, 6, 1);
        Assert.Equal(new DateOnly(2018, 6, 1), h.Service.Window().From);
        Assert.Equal(new DateTime(2026, 1, 1), h.Source.StartDate);
        // The snapshot holds nothing before its own coverage, so a start before it would only show "missing" rows: it is clamped to the coverage.
        h.Campaign.ReconciliationStartDate = new DateTime(1990, 1, 1);
        h.SnapshotOptions.SourceFromDate = new DateTime(2000, 1, 1);
        Assert.Equal(new DateOnly(2000, 1, 1), h.Service.Window().From);
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m)); h.Both(Harness.Row(32, "TP127-201", "V2", new(2026, 10, 5), 500m, 21));
        Assert.True((await h.Service.StartAsync(Manager, null, CancellationToken.None)).IsSuccess);
        Assert.Equal(new DateOnly(2000, 1, 1), Latest(h).From); Assert.Equal(new DateOnly(2000, 1, 1), h.Direct.LastWindow!.Value.From);
        // A passing run never sets the sign-off, however wide the window: without it the gate stays closed.
        Assert.Equal(ReconciliationStates.Passed, Latest(h).Status);
        h.Campaign.FinancialSourceValidated = false;
        Assert.False(h.Service.Gate().Reconciled); Assert.Equal(SourceGate.SignOffMissing, h.Service.Gate().Code);
    }

    [Fact]
    public async Task MatchingData_Passes_ForBothCompanies_AndOpensTheGate()
    {
        var h = new Harness();
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        h.Both(Harness.Row(32, "TP127-201", "V2", new(2026, 10, 5), 500m, 21));
        var start = await h.Service.StartAsync(Manager, null, CancellationToken.None);
        Assert.Equal(CollectionsOutcome.Accepted, start.Outcome);
        var run = Latest(h);
        Assert.Equal(ReconciliationStates.Passed, run.Status);
        Assert.All(run.Companies, c => Assert.Equal(ReconciliationStates.Passed, c.Status));
        var gate = h.Service.Gate();
        Assert.True(gate.Reconciled);
        Assert.Equal(SourceGate.Reconciled, gate.Code);
    }

    [Fact]
    public async Task ADifference_FailsThatCompany_ListsTheUnit_AndKeepsTheGateClosed_WhileTheOtherCompanyStillPasses()
    {
        var h = new Harness();
        h.Both(Harness.Row(32, "TP127-201", "V2", new(2026, 10, 5), 500m, 21));
        h.Snapshot.Rows.Add(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        h.Direct.Rows.Add(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 400m));
        await h.Service.StartAsync(Manager, null, CancellationToken.None);
        var run = Latest(h);
        Assert.Equal(ReconciliationStates.Failed, run.Status);
        Assert.Equal(ReconciliationStates.Failed, run.Companies.Single(c => c.CompanyId == 4).Status);
        Assert.Equal(ReconciliationStates.Passed, run.Companies.Single(c => c.CompanyId == 32).Status);
        Assert.Equal("TP140-101", run.Companies.Single(c => c.CompanyId == 4).Comparison!.Diffs.Single().UnitCode);
        var gate = h.Service.Gate();
        Assert.False(gate.Reconciled);
        Assert.Equal(SourceGate.EvidenceFailed, gate.Code);
        Assert.Contains("Tiger Group Dubai", gate.Message);
        // Per company: Dubai is closed (no refresh could be made to resolve the difference: this harness has no refresher), Sharjah is open and is said to be.
        Assert.False(gate.IsOpen(4)); Assert.True(gate.IsOpen(32));
        Assert.Contains("Tiger Group Sharjah is reconciled and can still be sent", gate.Message);
        Assert.Equal([4], gate.Closed([4, 32]).Select(c => c.CompanyId));
    }

    // ---------------------------------------------------------------- PACT is the source: a difference is first a synchronization to repair, with ONE controlled refresh

    [Fact]
    public async Task AChangedRemainingAmount_IsResolvedByOneRefreshOfThatCompany_AndTheRunPasses()
    {
        var h = new Harness(refresher: "Sync");
        h.Both(Harness.Row(32, "TP127-201", "V2", new(2026, 10, 5), 500m, 21));
        h.Snapshot.Rows.Add(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        h.Direct.Rows.Add(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 400m));                 // a payment since the last refresh
        await h.Service.StartAsync(Manager, null, CancellationToken.None);
        var run = Latest(h);
        Assert.Equal(ReconciliationStates.Passed, run.Status);
        var dubai = run.Companies.Single(c => c.CompanyId == 4);
        Assert.Equal(ReconciliationStates.Passed, dubai.Status);
        Assert.Equal((ReconciliationRecoveryReasons.Differences, "Succeeded", true, 1, true), (dubai.Recovery!.Reason, dubai.Recovery.RefreshStatus, dubai.Recovery.Refreshed, dubai.Recovery.MismatchedUnitsBefore, dubai.Recovery.Resolved));
        Assert.Equal("TP140-101", dubai.Recovery.ComparisonBefore!.Diffs.Single().UnitCode);           // the exact differences are recorded even though they are resolved
        Assert.Contains("Recovered", dubai.Message);
        Assert.Equal([(FinancialReconciliationService.RecoveryRefreshTrigger, (int?)4)], h.Refresher!.Calls);   // only the company that differed, once, under its own trigger
        Assert.Null(run.Companies.Single(c => c.CompanyId == 32).Recovery);
        Assert.True(dubai.Recovery.SnapshotAfterUtc > dubai.Recovery.SnapshotBeforeUtc);
        Assert.True(h.Service.Gate().Reconciled); Assert.Equal(SourceGate.Reconciled, h.Service.Gate().Code);
    }

    [Fact]
    public async Task ADeletedInstalment_ANewUnit_AndAMovedDueDate_AreAllResolvedByTheRefresh()
    {
        var h = new Harness(refresher: "Sync");
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        h.Snapshot.Rows.Add(Harness.Row(4, "TP140-101", "V9", new(2026, 9, 6), 77m));                 // paid / cancelled: no longer returned by PACT
        h.Direct.Rows.Add(Harness.Row(4, "TP140-102", "NEW", new(2026, 10, 7), 300m, 12));             // a new unit in PACT
        h.Snapshot.Rows.Add(Harness.Row(4, "TP140-103", "V3", new(2026, 9, 30), 50m, 13));
        h.Direct.Rows.Add(Harness.Row(4, "TP140-103", "V3", new(2026, 10, 2), 50m, 13));               // the due date moved across the month boundary
        await h.Service.StartAsync(Manager, 4, CancellationToken.None);
        var dubai = Assert.Single(Latest(h).Companies);
        Assert.Equal(ReconciliationStates.Passed, dubai.Status);
        Assert.Equal(3, dubai.Recovery!.MismatchedUnitsBefore);
        var kinds = dubai.Recovery.ComparisonBefore!.Diffs.SelectMany(d => d.Instalments).Select(i => i.Kind).OrderBy(k => k).ToArray();
        Assert.Equal([ReconDifferenceKinds.DueDateMismatch, ReconDifferenceKinds.MissingInSnapshot, ReconDifferenceKinds.MissingInSource], kinds);
        Assert.Single(h.Refresher!.Calls);
        // The snapshot now holds exactly what PACT returns: the new unit is in, the paid instalment is out, the date moved.
        Assert.Equal(h.Direct.Rows.Count, h.Snapshot.Rows.Count(r => r.CompanyId == 4));
    }

    [Fact]
    public async Task ADifferenceThatPersistsAfterTheRefresh_IsADefectToInvestigate_HoldsOnlyThatUnit_AndIsNeverRetriedAgain()
    {
        var h = new Harness(refresher: "NoChange");                                                    // the refresh publishes, but the mapping still differs
        h.Both(Harness.Row(32, "TP127-201", "V2", new(2026, 10, 5), 500m, 21));
        h.Both(Harness.Row(4, "TP140-100", "V0", new(2026, 9, 5), 10m, 10));
        h.Snapshot.Rows.Add(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        h.Direct.Rows.Add(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 400m));
        await h.Service.StartAsync(Manager, null, CancellationToken.None);
        var run = Latest(h);
        var dubai = run.Companies.Single(c => c.CompanyId == 4);
        Assert.Equal(ReconciliationStates.Failed, dubai.Status);
        Assert.True(dubai.PersistentAfterRefresh);
        Assert.Equal((true, false, 1), (dubai.Recovery!.Refreshed, dubai.Recovery.Resolved, dubai.Recovery.MismatchedUnitsBefore));
        Assert.Contains("synchronization or mapping defect", dubai.Message);
        Assert.Single(h.Refresher!.Calls);                                                              // one refresh per company per run: no endless retry
        // The gate: both companies open; the one unit that differs is held back, the other unit of Dubai and all of Sharjah are not.
        var gate = h.Service.Gate();
        Assert.True(gate.Reconciled); Assert.Equal(SourceGate.ReconciledWithHeldUnits, gate.Code);
        Assert.True(gate.IsOpen(4)); Assert.True(gate.IsOpen(32)); Assert.Empty(gate.Closed([4, 32]));
        var held = Assert.Single(gate.HeldUnits);
        Assert.Equal((4, "T11", 11L, "TP140-101"), (held.CompanyId, held.TenantId, held.UnitId, held.UnitCode));
        Assert.Contains((4, "T11", 11L), gate.HeldUnitKeys());
        Assert.Equal(1, gate.MismatchUnits);
        Assert.Contains("defect", gate.Message);
    }

    [Fact]
    public async Task WhenTheRefreshFails_ThePreviousSnapshotStays_TheCompanyStaysClosed_AndTheOtherCompanyIsOpen()
    {
        var h = new Harness(refresher: "Fail");
        h.Both(Harness.Row(32, "TP127-201", "V2", new(2026, 10, 5), 500m, 21));
        h.Snapshot.Rows.Add(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        h.Direct.Rows.Add(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 400m));
        await h.Service.StartAsync(Manager, null, CancellationToken.None);
        var dubai = Latest(h).Companies.Single(c => c.CompanyId == 4);
        Assert.Equal(ReconciliationStates.Failed, dubai.Status);
        Assert.False(dubai.PersistentAfterRefresh);
        Assert.Equal(("Failed", false), (dubai.Recovery!.RefreshStatus, dubai.Recovery.Refreshed));
        Assert.Contains("could not be refreshed", dubai.Message); Assert.Contains("PACT timed out", dubai.Message);
        Assert.Equal(1000m, h.Snapshot.Rows.Single(r => r.CompanyId == 4).Amount);                      // nothing was published from the failed attempt
        var gate = h.Service.Gate();
        Assert.False(gate.Reconciled); Assert.Equal(SourceGate.EvidenceFailed, gate.Code);
        Assert.False(gate.IsOpen(4)); Assert.True(gate.IsOpen(32)); Assert.Empty(gate.HeldUnits);        // closed as a whole: no consistent refresh confirmed the other units
        Assert.Single(h.Refresher!.Calls);
        // A refresh that is busy (another refresh of the company holds its lock) is reported the same way, without a second attempt.
        var busy = new Harness(refresher: "AlreadyRunning");
        busy.Snapshot.Rows.Add(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m)); busy.Direct.Rows.Add(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 400m));
        await busy.Service.StartAsync(Manager, 4, CancellationToken.None);
        Assert.Equal("AlreadyRunning", Assert.Single(Latest(busy).Companies).Recovery!.RefreshStatus);
        Assert.Single(busy.Refresher!.Calls);
    }

    [Fact]
    public async Task AStaleSnapshot_IsRefreshedBeforeTheComparison_InsteadOfAnError()
    {
        var h = new Harness(refresher: "Sync");
        h.Snapshot.Companies[4] = FakeSnapshotSource.Healthy(4, Now.AddMinutes(-120));
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        await h.Service.StartAsync(Manager, 4, CancellationToken.None);
        var dubai = Assert.Single(Latest(h).Companies);
        Assert.Equal(ReconciliationStates.Passed, dubai.Status);
        Assert.Equal((ReconciliationRecoveryReasons.StaleSnapshot, true, true, 0), (dubai.Recovery!.Reason, dubai.Recovery.Refreshed, dubai.Recovery.Resolved, dubai.Recovery.MismatchedUnitsBefore));
        Assert.Equal(1, h.Direct.Reads);                                                                // PACT is read once, for the comparison of the fresh snapshot
        // The recovery can be switched off: then the first comparison stands as it is (here: the old snapshot is an Error, as before).
        var off = new Harness(refresher: "Sync");
        off.Campaign.ReconciliationRecoveryRefresh = false;
        off.Snapshot.Companies[4] = FakeSnapshotSource.Healthy(4, Now.AddMinutes(-120));
        off.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        await off.Service.StartAsync(Manager, 4, CancellationToken.None);
        Assert.Equal(ReconciliationStates.Error, Assert.Single(Latest(off).Companies).Status);
        Assert.Empty(off.Refresher!.Calls);
    }

    // ---------------------------------------------------------------- the evidence follows the snapshot: a refresh supersedes it, and the check runs after each refresh

    [Fact]
    public async Task ARefreshAfterAPassingCheck_SupersedesTheEvidenceOfThatCompanyOnly_UntilTheCheckRunsAgain()
    {
        var h = new Harness(refresher: "Sync");
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m)); h.Both(Harness.Row(32, "TP127-201", "V2", new(2026, 10, 5), 500m, 21));
        await h.Service.StartAsync(Manager, null, CancellationToken.None);
        Assert.True(h.Service.Gate().Reconciled);
        // 1. The refresh job of this process publishes Dubai again (noted on the evidence): Dubai's pass no longer describes the data in use; Sharjah's still does.
        h.Clock = Now.AddMinutes(30);
        h.History.NoteSnapshotPublished(4, h.Clock);
        var gate = h.Service.Gate();
        Assert.False(gate.Reconciled); Assert.Equal(SourceGate.EvidenceSuperseded, gate.Code);
        Assert.False(gate.IsOpen(4)); Assert.True(gate.IsOpen(32));
        Assert.Contains("republished", gate.Message);
        // 2. The same from a caller that read the snapshot itself: a publish time that differs from the one the check compared.
        var fresh = new Harness(refresher: "Sync");
        fresh.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m)); fresh.Both(Harness.Row(32, "TP127-201", "V2", new(2026, 10, 5), 500m, 21));
        await fresh.Service.StartAsync(Manager, null, CancellationToken.None);
        var checkedAt = fresh.History.Latest()!.Companies.Single(c => c.CompanyId == 32).SnapshotSuccessUtc;
        var bound = SourceGate.Evaluate(fresh.Campaign, fresh.Source, fresh.History, Now, new Dictionary<int, DateTime?> { [4] = Now.AddMinutes(-10), [32] = checkedAt!.Value.AddMinutes(5) });
        Assert.True(bound.IsOpen(4)); Assert.False(bound.IsOpen(32)); Assert.Equal(SourceGate.EvidenceSuperseded, bound.Code);
        // 3. The check that follows the refresh (the job calls it) covers the new publish: Dubai is open again, by its own run, without a cooldown and without touching Sharjah.
        var after = await h.Service.RunAfterRefreshAsync(4, CancellationToken.None);
        Assert.NotNull(after);
        Assert.Equal((ReconciliationTriggers.AfterRefresh, 4, ReconciliationStates.Passed, FinancialReconciliationService.SystemActor), (after!.Trigger, after.CompanyScope, after.Status, after.RequestedBy));
        Assert.Equal(4, Assert.Single(after.Companies).CompanyId);
        Assert.True(h.Service.Gate().Reconciled);
        Assert.Equal(0, h.Launcher.Launches - 1);                                                       // it ran inline in the caller's job, not through the launcher
    }

    [Fact]
    public async Task TheCheckAfterARefresh_SkipsWhileARunOfThatCompanyIsInProgress_AndIsOffByConfiguration()
    {
        var h = new Harness(refresher: "Sync");
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m)); h.Both(Harness.Row(32, "TP127-201", "V2", new(2026, 10, 5), 500m, 21));
        // A run of both companies is in progress: the after-refresh check of Dubai does not start a second comparison of it.
        var running = new ReconciliationRunDto(Guid.NewGuid(), ReconciliationStates.Running, Now, null, new(2014, 1, 1), new(2026, 10, 31), null, 0.01m, "x", [], ReconciliationTriggers.AfterRefresh);
        Assert.True(h.History.TryBegin(running, TimeSpan.Zero, out _));
        Assert.Null(await h.Service.RunAfterRefreshAsync(4, CancellationToken.None));
        // A run of the OTHER company in progress does not stop it.
        h.History.Replace(running with { Status = ReconciliationStates.Error, FinishedUtc = Now });
        var sharjah = new ReconciliationRunDto(Guid.NewGuid(), ReconciliationStates.Running, Now, null, new(2014, 1, 1), new(2026, 10, 31), 32, 0.01m, "x", [], ReconciliationTriggers.AfterRefresh);
        Assert.True(h.History.TryBegin(sharjah, TimeSpan.Zero, out _));
        Assert.NotNull(await h.Service.RunAfterRefreshAsync(4, CancellationToken.None));
        h.History.Replace(sharjah with { Status = ReconciliationStates.Error, FinishedUtc = Now });
        // A person's cooldown never delays it (it follows the refresh schedule), while a person's second click is still refused.
        Assert.Equal(CollectionsOutcome.Accepted, (await h.Service.StartAsync(Manager, 32, CancellationToken.None)).Outcome);
        Assert.Equal(CollectionsOutcome.Conflict, (await h.Service.StartAsync(Manager, 32, CancellationToken.None)).Outcome);
        Assert.NotNull(await h.Service.RunAfterRefreshAsync(32, CancellationToken.None));
        // Switched off: nothing runs after a refresh, but the publish is still noted (the old evidence is superseded rather than silently reused).
        h.Campaign.ReconcileAfterRefresh = false;
        h.Clock = h.Clock.AddMinutes(5);
        Assert.Null(await h.Service.RunAfterRefreshAsync(32, CancellationToken.None));
        Assert.Equal(SourceGate.EvidenceSuperseded, h.Service.Gate().Company(32)!.Code);
        h.Campaign.ReconcileAfterRefresh = true; h.Campaign.RequireAutomatedReconciliation = false;
        Assert.Null(await h.Service.RunAfterRefreshAsync(32, CancellationToken.None));
        Assert.True(h.Service.Gate().Reconciled);                                                      // sign-off only, by configuration
    }

    [Fact]
    public void TheDefaults_RunTheCheckAfterEachRefresh_AndRepairOnce()
    {
        var options = new CollectionsCampaignOptions();
        Assert.True(options.ReconcileAfterRefresh); Assert.True(options.ReconciliationRecoveryRefresh);
        Assert.False(options.FinancialSourceValidated); Assert.False(options.AutoSendingEnabled);        // neither is ever set by a passing check
    }

    [Fact]
    public async Task PactUnreachable_IsAnError_NotAFailureAndNotAPass()
    {
        var h = new Harness();
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        h.Direct.Throw = new PactReceivablesSourceException("The PACT report connection is not configured.");
        await h.Service.StartAsync(Manager, 4, CancellationToken.None);
        var company = Assert.Single(Latest(h).Companies);
        Assert.Equal(ReconciliationStates.Error, company.Status);
        Assert.Null(company.Comparison);
        Assert.Equal(SourceGate.EvidenceError, h.Service.Gate().Code);
    }

    [Fact]
    public async Task AnOldSnapshot_IsNotCompared_BecausePaymentsSinceTheRefreshWouldLookLikeDifferences()
    {
        var h = new Harness();
        h.Snapshot.Companies[4] = FakeSnapshotSource.Healthy(4, Now.AddMinutes(-120));
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        await h.Service.StartAsync(Manager, 4, CancellationToken.None);
        var company = Assert.Single(Latest(h).Companies);
        Assert.Equal(ReconciliationStates.Error, company.Status);
        Assert.Contains("minutes old", company.Message);
        Assert.Equal(0, h.Direct.Reads);                                    // PACT is not read for a comparison that cannot be exact
    }

    [Fact]
    public async Task ARefreshInProgress_And_NeverLoaded_AreErrors()
    {
        var h = new Harness();
        h.Snapshot.Companies[4] = FakeSnapshotSource.Healthy(4, Now.AddMinutes(-5)) with { RefreshInProgress = true, RefreshStartedUtc = Now.AddMinutes(-1) };
        h.Snapshot.Companies[32] = FakeSnapshotSource.NeverLoaded(32);
        await h.Service.StartAsync(Manager, null, CancellationToken.None);
        var run = Latest(h);
        Assert.Contains("refresh of this company is running", run.Companies.Single(c => c.CompanyId == 4).Message);
        Assert.Contains("No snapshot has been loaded", run.Companies.Single(c => c.CompanyId == 32).Message);
        Assert.Equal(ReconciliationStates.Error, run.Status);
    }

    [Fact]
    public async Task NothingOnEitherSide_IsAnError_NotAVacuousPass()
    {
        var h = new Harness();
        await h.Service.StartAsync(Manager, 4, CancellationToken.None);
        Assert.Equal(ReconciliationStates.Error, Assert.Single(Latest(h).Companies).Status);
    }

    [Fact]
    public async Task EvidenceNeverStartedByAPageRead_AndRunsAreSingleFlightWithACooldown()
    {
        var h = new Harness();
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        _ = await h.Service.OverviewAsync(Manager, CancellationToken.None);
        _ = h.Service.Gate();
        Assert.Equal((0, 0), (h.Launcher.Launches, h.Direct.Reads));         // reading the status never reads PACT
        Assert.Equal(CollectionsOutcome.Accepted, (await h.Service.StartAsync(Manager, 4, CancellationToken.None)).Outcome);
        var second = await h.Service.StartAsync(Manager, 4, CancellationToken.None);   // clicked again at once
        Assert.Equal(CollectionsOutcome.Conflict, second.Outcome);
        Assert.Equal(1, h.Launcher.Launches);
        h.Clock = Now.AddMinutes(3);
        h.Snapshot.Companies[4] = FakeSnapshotSource.Healthy(4, h.Clock.AddMinutes(-5));
        Assert.Equal(CollectionsOutcome.Accepted, (await h.Service.StartAsync(Manager, 4, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task OnlyAPersonWhoMaySendCanStartOne_AndAnyReaderCanSeeTheResult()
    {
        var h = new Harness();
        var reader = new CollectionsCaller(Guid.NewGuid(), [], []);
        Assert.Equal(CollectionsOutcome.Forbidden, (await h.Service.StartAsync(reader, null, CancellationToken.None)).Outcome);
        Assert.Equal(0, h.Launcher.Launches);
        Assert.Equal(CollectionsOutcome.InvalidRequest, (await h.Service.StartAsync(Manager, 7, CancellationToken.None)).Outcome);
        var noLauncher = new Harness(launcher: false);
        Assert.Equal(CollectionsOutcome.Disabled, (await noLauncher.Service.StartAsync(Manager, null, CancellationToken.None)).Outcome);
    }

    // ---------------------------------------------------------------- the gate

    [Fact]
    public void WithoutASignOff_TheGateIsClosed_WhateverTheEvidenceSays()
    {
        var h = new Harness { };
        h.Campaign.FinancialSourceValidated = false;
        var gate = h.Service.Gate();
        Assert.False(gate.Reconciled); Assert.Equal(SourceGate.SignOffMissing, gate.Code); Assert.False(gate.SignOffRecorded);
    }

    [Fact]
    public void ASignOffForAnotherProcedure_DoesNotCount()
    {
        var h = new Harness();
        h.Campaign.ValidatedProcedureSuffix = ""; h.Source.ProcedureSuffix = "V2";
        Assert.Equal(SourceGate.ProcedureChanged, h.Service.Gate().Code);
    }

    [Fact]
    public void WithASignOffButNoRun_TheGateSaysEvidenceIsMissing_NotThatTheDataIsWrong()
    {
        var gate = new Harness().Service.Gate();
        Assert.False(gate.Reconciled); Assert.Equal(SourceGate.EvidenceNotRun, gate.Code);
        Assert.True(gate.SignOffRecorded);
    }

    [Fact]
    public async Task APassingRunExpiresAfterTheConfiguredHours_AndALaterErrorDoesNotRevokeIt()
    {
        var h = new Harness();
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m));
        h.Both(Harness.Row(32, "TP127-201", "V2", new(2026, 10, 5), 500m, 21));
        await h.Service.StartAsync(Manager, null, CancellationToken.None);
        Assert.True(h.Service.Gate().Reconciled);
        h.Clock = Now.AddMinutes(10); h.Snapshot.Companies[4] = FakeSnapshotSource.Healthy(4, h.Clock.AddMinutes(-5));
        h.Direct.Throw = new PactReceivablesSourceException("down");
        await h.Service.StartAsync(Manager, 4, CancellationToken.None);          // an Error run
        Assert.True(h.Service.Gate().Reconciled);                                 // earlier conclusive evidence stands
        h.Clock = Now.AddHours(25);
        Assert.Equal(SourceGate.EvidenceExpired, h.Service.Gate().Code);
    }

    [Fact]
    public void TheAutomatedRequirementCanBeSwitchedOff_Explicitly()
    {
        var h = new Harness();
        h.Campaign.RequireAutomatedReconciliation = false;
        Assert.True(h.Service.Gate().Reconciled);
    }

    [Fact]
    public void WithoutAnyEvidenceSource_TheGateFallsBackToTheSignOffOnly()
    {
        var gate = SourceGate.Evaluate(new CollectionsCampaignOptions { FinancialSourceValidated = true }, new PactReceivablesOptions(), null, Now);
        Assert.True(gate.Reconciled);
    }

    // ---------------------------------------------------------------- what the check is, and is not

    [Fact]
    public async Task APassingRun_IsLabelledSnapshotConsistency_NeverAnIndependentLedgerVerification()
    {
        var h = new Harness();
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m)); h.Both(Harness.Row(32, "TP127-201", "V2", new(2026, 10, 5), 500m, 21));
        await h.Service.StartAsync(Manager, null, CancellationToken.None);
        var gate = h.Service.Gate();
        Assert.True(gate.Reconciled);
        Assert.Equal("SnapshotConsistency", gate.Basis);
        Assert.False(ReconciliationBasis.IsIndependentLedgerVerification);
        Assert.Contains("not an independent ledger verification", gate.Message);
        Assert.Contains("usp_CollectionsRcv_Refresh", Latest(h).Companies[0].SnapshotOrigin);
        Assert.Contains("same PACT procedure", ReconciliationBasis.DoesNotProve + " same PACT procedure");
        Assert.Contains("human sign-off", ReconciliationBasis.DoesNotProve);
    }

    [Fact]
    public async Task APassingConsistencyCheck_NeverReplacesTheSignOff_AndTheSignOffNeverReplacesTheCheck()
    {
        var h = new Harness();
        h.Both(Harness.Row(4, "TP140-101", "V1", new(2026, 9, 5), 1000m)); h.Both(Harness.Row(32, "TP127-201", "V2", new(2026, 10, 5), 500m, 21));
        h.Campaign.FinancialSourceValidated = false;                       // the check passes, nobody has signed off
        await h.Service.StartAsync(Manager, null, CancellationToken.None);
        Assert.Equal(ReconciliationStates.Passed, Latest(h).Status);
        var noSignOff = h.Service.Gate();
        Assert.False(noSignOff.Reconciled); Assert.Equal(SourceGate.SignOffMissing, noSignOff.Code); Assert.False(noSignOff.LedgerVerificationAttested);

        h.Campaign.FinancialSourceValidated = true;                         // signed off but no check on record (fresh process)
        var fresh = SourceGate.Evaluate(h.Campaign, h.Source, new ReconciliationHistory(), Now);
        Assert.False(fresh.Reconciled); Assert.Equal(SourceGate.EvidenceNotRun, fresh.Code); Assert.True(fresh.LedgerVerificationAttested);
    }

    [Fact]
    public void AConfiguredProcedureSuffix_IsWarnedAbout_BecauseTheSnapshotRefreshNeverReadsIt()
    {
        var campaign = new CollectionsCampaignOptions { FinancialSourceValidated = true, ValidatedProcedureSuffix = "V2", RequireAutomatedReconciliation = false };
        var gate = SourceGate.Evaluate(campaign, new PactReceivablesOptions { ProcedureSuffix = "V2" }, null, Now);
        Assert.Contains("p4/p32AccountReceivables", gate.Warning);
        Assert.Null(SourceGate.Evaluate(new CollectionsCampaignOptions { FinancialSourceValidated = true }, new PactReceivablesOptions(), null, Now).Warning);
    }
}
