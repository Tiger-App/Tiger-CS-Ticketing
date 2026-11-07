using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Application.Modules.Collections.Reconciliation;

/// <summary>States of a reconciliation (a run, or one company within it). Missing evidence and real differences are different states on purpose.</summary>
public static class ReconciliationStates
{
    /// <summary>No reconciliation has run (or the application restarted since): nothing is known.</summary>
    public const string NotRun = "NotRun";
    public const string Running = "Running";
    /// <summary>The snapshot and the PACT procedure agree for every unit, within the tolerance.</summary>
    public const string Passed = "Passed";
    /// <summary>The comparison completed and found units that differ: the listed units say which and why.</summary>
    public const string Failed = "Failed";
    /// <summary>The comparison could not be completed (PACT unreachable, snapshot not fresh, snapshot republished meanwhile): NOT a finding about the data.</summary>
    public const string Error = "Error";
    /// <summary>A passing result older than <c>ReconciliationMaxAgeHours</c>.</summary>
    public const string Expired = "Expired";
    /// <summary>The snapshot was republished after the last conclusive check, so that check no longer describes the data in use; the new publish has not been checked yet.</summary>
    public const string Superseded = "Superseded";
}

/// <summary>Who started a reconciliation run.</summary>
public static class ReconciliationTriggers
{
    /// <summary>A person pressed Run on the diagnostics page.</summary>
    public const string Manual = "Manual";
    /// <summary>The scheduled (or retried) snapshot refresh of a company succeeded and the check followed by itself.</summary>
    public const string AfterRefresh = "AfterRefresh";
}

/// <summary>
/// What the run did when the first comparison of a company found differences (or the snapshot was too old to compare): the controlled refresh of that company and the second comparison.
/// <c>Refreshed</c> is true only when the refresh published a new snapshot; <c>Resolved</c> is true when the second comparison matched. A company whose differences persist after a
/// successful refresh has a synchronization or mapping defect that needs investigation; it is never retried endlessly (one refresh per company per run).
/// </summary>
public sealed record ReconciliationRecoveryDto(string Reason, string RefreshStatus, string? RefreshMessage, bool Refreshed, int MismatchedUnitsBefore, bool Resolved,
    DateTime? SnapshotBeforeUtc, DateTime? SnapshotAfterUtc, ReconComparison? ComparisonBefore = null);

public static class ReconciliationRecoveryReasons
{
    /// <summary>The first comparison found differences.</summary>
    public const string Differences = "Differences";
    /// <summary>The snapshot was older than <c>ReconciliationMaxSnapshotAgeMinutes</c>, so it was refreshed before anything was compared.</summary>
    public const string StaleSnapshot = "StaleSnapshot";
}

public sealed record ReconciliationCompanyResultDto(
    int CompanyId, string CompanyName, string Status, string? Message, DateTime StartedUtc, DateTime FinishedUtc, DateTime? SnapshotSuccessUtc,
    string Procedure, ReconComparison? Comparison, string? SnapshotOrigin = null, ReconciliationRecoveryDto? Recovery = null)
{
    /// <summary>True when the differences were confirmed again after a successful refresh of this company: not a timing difference, a defect of the synchronization or the mapping.</summary>
    public bool PersistentAfterRefresh => Status == ReconciliationStates.Failed && Recovery is { Refreshed: true, Resolved: false };
}

/// <summary>
/// What the automated reconciliation compares and, just as important, what it does NOT establish. Both sides are the output of the SAME PACT procedure: the snapshot is that
/// procedure's result copied by the refresh job (linked server), the other side is that procedure called again over a direct connection. Agreement therefore proves the
/// snapshot is a faithful, current copy; it does not prove the procedure's figures are right against PACT's ledger. That is only ever attested by the recorded human sign-off.
/// </summary>
public static class ReconciliationBasis
{
    public const string Kind = "SnapshotConsistency";
    public const bool IsIndependentLedgerVerification = false;

    public static string SnapshotSide(int companyId) =>
        $"Snapshot: dbo.CollectionsRcvLine as published by usp_CollectionsRcv_Refresh, which calls [10.10.10.94].[PACTRPT].dbo.p{companyId}AccountReceivables over the linked server.";

    public static string SourceSide(int companyId) =>
        $"Compared with: dbo.p{companyId}AccountReceivables called again now over the direct PACTRPT connection (the same procedure; the configured ProcedureSuffix is not used because the snapshot ignores it).";

    public const string Proves = "The local snapshot equals what the PACT procedure returns now (the refresh copied it faithfully and nothing changed since).";
    public const string DoesNotProve = "That the procedure's remaining amounts, unit allocation or Due/Overdue dates are correct against PACT's own ledger records. Only the recorded human sign-off attests that.";
}

public sealed record ReconciliationRunDto(
    Guid RunId, string Status, DateTime StartedUtc, DateTime? FinishedUtc, DateOnly From, DateOnly Through, int? CompanyScope, decimal Tolerance, string? RequestedBy,
    IReadOnlyList<ReconciliationCompanyResultDto> Companies, string Trigger = ReconciliationTriggers.Manual);

/// <summary>A unit whose snapshot data differs persistently from PACT: held back from sending until the difference is investigated and resolved.</summary>
public sealed record ReconHeldUnit(int CompanyId, string TenantId, long UnitId, string UnitCode);

/// <summary>The gate of ONE company: whether its customers may be approved and sent, and which of its units are held back individually.</summary>
public sealed record SourceGateCompanyDto(int CompanyId, string CompanyName, bool Reconciled, string Code, string Message, DateTime? EvidenceAtUtc, DateTime? SnapshotSuccessUtc,
    IReadOnlyList<ReconHeldUnit> HeldUnits);

/// <summary>
/// Whether the financial source may be used for approval and sending, and exactly why not when it may not. <c>Reconciled</c> is true only when every company is; <c>Companies</c> says it per
/// company, so one company's missing or failed evidence never holds the other back, and <c>HeldUnits</c> are the units (of reconciled companies) whose data differs persistently from PACT: those
/// are held back one by one instead of closing the whole gate.
/// </summary>
public sealed record SourceGateDto(bool Reconciled, string Code, string Message, DateTime? EvidenceAtUtc, int MismatchUnits, bool SignOffRecorded, string? Reference,
    string Basis = ReconciliationBasis.Kind, bool LedgerVerificationAttested = false, string? Warning = null, IReadOnlyList<SourceGateCompanyDto>? Companies = null)
{
    public IReadOnlyList<ReconHeldUnit> HeldUnits => Companies?.SelectMany(c => c.HeldUnits).ToList() ?? [];

    /// <summary>Whether the customers of <paramref name="companyId"/> may be approved and sent (its held units excepted). Without per-company detail the overall decision applies.</summary>
    public bool IsOpen(int companyId) => Companies?.FirstOrDefault(c => c.CompanyId == companyId) is { } company ? company.Reconciled : Reconciled;

    public SourceGateCompanyDto? Company(int companyId) => Companies?.FirstOrDefault(c => c.CompanyId == companyId);

    /// <summary>The companies among <paramref name="companies"/> whose gate is closed.</summary>
    public IReadOnlyList<SourceGateCompanyDto> Closed(IEnumerable<int> companies) =>
        companies.Distinct().Select(c => Company(c) ?? new SourceGateCompanyDto(c, SourceGate.CompanyName(c), Reconciled, Code, Message, EvidenceAtUtc, null, [])).Where(c => !c.Reconciled).ToList();

    /// <summary>The held units as the record builder looks them up: company, tenant and unit id.</summary>
    public IReadOnlySet<(int CompanyId, string TenantId, long UnitId)> HeldUnitKeys() =>
        HeldUnits.Select(u => (u.CompanyId, u.TenantId.Trim(), u.UnitId)).ToHashSet();
}

public sealed record ReconciliationOverviewDto(SourceGateDto Gate, ReconciliationRunDto? Latest, IReadOnlyList<ReconciliationRunDto> Recent);

/// <summary>What the gate needs from the reconciliation side (kept small so Campaigns / Review depend on an abstraction, not on the service).</summary>
public interface IFinancialReconciliationEvidence
{
    /// <summary>The per-company evidence as of <paramref name="nowUtc"/>: one entry per company 4 and 32.</summary>
    IReadOnlyList<ReconciliationEvidenceItem> Evidence(DateTime nowUtc, int maxAgeHours);

    /// <summary>
    /// The same, bound to the snapshot in use: <paramref name="snapshotPublishedUtc"/> is each company's current publish time (<c>LastSuccessUtc</c>) as the caller just read it. Evidence
    /// taken against another publish is <see cref="ReconciliationStates.Superseded"/>: the data in use has not been checked.
    /// </summary>
    IReadOnlyList<ReconciliationEvidenceItem> Evidence(DateTime nowUtc, int maxAgeHours, IReadOnlyDictionary<int, DateTime?>? snapshotPublishedUtc) => Evidence(nowUtc, maxAgeHours);

    /// <summary>Tells the evidence that a company's snapshot was republished (by the refresh job of this process): evidence taken before it no longer describes the data in use.</summary>
    void NoteSnapshotPublished(int companyId, DateTime publishedUtc) { }
}

/// <summary>
/// One company's evidence. <c>HeldUnits</c> are the units that differ (every one of them when <c>HeldUnitsComplete</c>; otherwise only the first ones kept for diagnostics);
/// <c>PersistentAfterRefresh</c> says the differences were confirmed again after a successful refresh of the company: a defect, not a timing difference.
/// </summary>
public sealed record ReconciliationEvidenceItem(int CompanyId, string State, DateTime? AtUtc, int MismatchUnits, string? Message, DateTime? SnapshotSuccessUtc = null,
    IReadOnlyList<ReconHeldUnit>? HeldUnits = null, bool HeldUnitsComplete = true, bool PersistentAfterRefresh = false);

/// <summary>Launches a reconciliation run outside the request (background). Implemented in Infrastructure.</summary>
public interface IReconciliationLauncher
{
    void Launch(Guid runId);
}

/// <summary>The snapshot-consistency check that follows a successful snapshot refresh of one company (called by the refresh jobs; never by a page).</summary>
public interface IPostRefreshReconciliation
{
    /// <summary>Checks <paramref name="companyId"/> against a fresh direct read of its PACT procedure, right after its snapshot was published. Never throws; returns the run, or null when not run.</summary>
    Task<ReconciliationRunDto?> RunAfterRefreshAsync(int companyId, CancellationToken cancellationToken);
}

/// <summary>Reads the same PACT procedure the snapshot is built from, directly and independently of the snapshot.</summary>
public interface IReconciliationSource
{
    string ProcedureLabel(int companyId);
    Task<IReadOnlyList<TigerCS.Application.Modules.Collections.Abstractions.PactReceivableInstalment>> ReadAsync(int companyId, DateOnly from, DateOnly through, CancellationToken cancellationToken);
}
