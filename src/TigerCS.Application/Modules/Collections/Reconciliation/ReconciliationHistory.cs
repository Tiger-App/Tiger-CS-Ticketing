namespace TigerCS.Application.Modules.Collections.Reconciliation;

/// <summary>
/// The reconciliation runs of this process, newest first (a registered singleton). Deliberately in memory: after a restart there is no evidence and the gate stays closed until a
/// run is made again - the safe direction (the refresh that runs at start-up reconciles by itself, so the evidence is back within minutes). It is also the single-flight / cooldown guard,
/// so neither page views nor repeated clicks can start a second PACT read of a company while one runs. It also remembers when each company's snapshot was last republished in this process,
/// so evidence taken against an older publish is reported as <see cref="ReconciliationStates.Superseded"/> instead of covering data it never saw.
/// </summary>
public sealed class ReconciliationHistory : IFinancialReconciliationEvidence
{
    private const int Keep = 20;
    private readonly object _gate = new();
    private readonly List<ReconciliationRunDto> _runs = [];
    private readonly Dictionary<int, DateTime> _published = [];
    private DateTime _lastManualStartUtc = DateTime.MinValue;

    /// <summary>
    /// Registers a new running run. Refused while a run whose company scope overlaps is running (a run of both companies overlaps everything), and, for a person's run only, while the
    /// last person's run started less than <paramref name="cooldown"/> ago. A system run (after a refresh) has no cooldown: it follows the refresh schedule, not a click.
    /// </summary>
    public bool TryBegin(ReconciliationRunDto run, TimeSpan cooldown, out string? refusal)
    {
        lock (_gate)
        {
            var manual = run.Trigger == ReconciliationTriggers.Manual;
            if (_runs.Any(r => r.Status == ReconciliationStates.Running && Overlaps(r.CompanyScope, run.CompanyScope))) { refusal = "A reconciliation is already running."; return false; }
            if (manual && run.StartedUtc - _lastManualStartUtc < cooldown) { refusal = $"A reconciliation was started less than {cooldown.TotalMinutes:0} minutes ago; wait for its result."; return false; }
            if (manual) _lastManualStartUtc = run.StartedUtc;
            _runs.Insert(0, run);
            if (_runs.Count > Keep) _runs.RemoveRange(Keep, _runs.Count - Keep);
            refusal = null;
            return true;
        }
    }

    private static bool Overlaps(int? a, int? b) => a is null || b is null || a == b;

    public void Replace(ReconciliationRunDto run)
    {
        lock (_gate)
        {
            var i = _runs.FindIndex(r => r.RunId == run.RunId);
            if (i >= 0) _runs[i] = run;
        }
    }

    public ReconciliationRunDto? Get(Guid runId) { lock (_gate) return _runs.FirstOrDefault(r => r.RunId == runId); }
    public ReconciliationRunDto? Latest() { lock (_gate) return _runs.FirstOrDefault(); }
    public IReadOnlyList<ReconciliationRunDto> Recent() { lock (_gate) return [.. _runs]; }

    public void NoteSnapshotPublished(int companyId, DateTime publishedUtc)
    {
        lock (_gate) { if (!_published.TryGetValue(companyId, out var known) || publishedUtc > known) _published[companyId] = publishedUtc; }
    }

    public IReadOnlyList<ReconciliationEvidenceItem> Evidence(DateTime nowUtc, int maxAgeHours) => Evidence(nowUtc, maxAgeHours, null);

    /// <summary>
    /// Per company, the newest CONCLUSIVE result (Passed or Failed) decides; an Error (PACT unreachable, snapshot not fresh) never revokes earlier evidence but is reported when nothing
    /// conclusive exists. A pass older than the maximum age is Expired. Evidence taken against a snapshot that has since been republished (a newer publish noted in this process, or a
    /// current publish time passed in that differs from the one the check saw) is Superseded: it says nothing about the data in use.
    /// </summary>
    public IReadOnlyList<ReconciliationEvidenceItem> Evidence(DateTime nowUtc, int maxAgeHours, IReadOnlyDictionary<int, DateTime?>? snapshotPublishedUtc)
    {
        lock (_gate)
        {
            var items = new List<ReconciliationEvidenceItem>();
            foreach (var company in new[] { 4, 32 })
            {
                var results = _runs.Where(r => r.Status != ReconciliationStates.Running)
                    .SelectMany(r => r.Companies.Where(c => c.CompanyId == company)).OrderByDescending(c => c.FinishedUtc).ToList();
                var conclusive = results.FirstOrDefault(c => c.Status is ReconciliationStates.Passed or ReconciliationStates.Failed);
                if (conclusive is not null)
                {
                    var held = conclusive.Comparison?.Diffs.Select(d => new ReconHeldUnit(company, d.TenantId, d.UnitId, d.UnitCode)).ToList() ?? [];
                    var complete = conclusive.Comparison is not { DiffsTruncated: true };
                    string state;
                    string? message = conclusive.Message;
                    if (IsSuperseded(company, conclusive, snapshotPublishedUtc, out var publishedAt))
                    {
                        state = ReconciliationStates.Superseded;
                        message = $"The snapshot was republished at {publishedAt:yyyy-MM-dd HH:mm:ss} UTC after the last check (snapshot of {conclusive.SnapshotSuccessUtc:yyyy-MM-dd HH:mm:ss} UTC); the data in use has not been checked yet.";
                    }
                    else state = conclusive.Status == ReconciliationStates.Passed && nowUtc - conclusive.FinishedUtc > TimeSpan.FromHours(maxAgeHours) ? ReconciliationStates.Expired : conclusive.Status;
                    items.Add(new(company, state, conclusive.FinishedUtc, conclusive.Comparison?.MismatchedUnits ?? 0, message, conclusive.SnapshotSuccessUtc, held, complete, conclusive.PersistentAfterRefresh));
                }
                else if (results.FirstOrDefault() is { } error) items.Add(new(company, ReconciliationStates.Error, error.FinishedUtc, 0, error.Message, error.SnapshotSuccessUtc));
                else items.Add(new(company, ReconciliationStates.NotRun, null, 0, null));
            }
            return items;
        }
    }

    private bool IsSuperseded(int company, ReconciliationCompanyResultDto conclusive, IReadOnlyDictionary<int, DateTime?>? current, out DateTime? publishedAt)
    {
        publishedAt = null;
        // A publish this process noted after the check started: the check could not have seen it.
        if (_published.TryGetValue(company, out var noted) && noted > conclusive.StartedUtc) { publishedAt = noted; return true; }
        // The caller's own reading of the current publish time differs from the one the check compared.
        if (current is not null && current.TryGetValue(company, out var now) && now is not null && conclusive.SnapshotSuccessUtc is not null && now != conclusive.SnapshotSuccessUtc)
        { publishedAt = now; return true; }
        return false;
    }
}
