using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Ticketing.Services;

/// <summary>
/// Settings of the one-time default-classification backfill (config section <c>DefaultClassificationBackfill</c>). There is NO scheduler: it runs
/// only when a System Administrator calls the admin endpoint, and a LIVE run additionally needs <see cref="Enabled"/> = true (default false).
/// A preview or dry run always works and writes nothing.
/// </summary>
public sealed class DefaultClassificationBackfillOptions
{
    public const string SectionName = "DefaultClassificationBackfill";

    /// <summary>Gate for LIVE (writing) runs. Pinned false in Production configuration until the run is separately approved.</summary>
    public bool Enabled { get; set; }

    public int DefaultBatchSize { get; set; } = 50;

    public int MaxBatchSize { get; set; } = 500;

    /// <summary>
    /// Tickets that have a resolvable category but no request type were classified by category alone and are usually deliberate. They are
    /// reported but LEFT ALONE unless this is switched on for the approved run.
    /// </summary>
    public bool IncludeCategoryOnly { get; set; }
}

/// <summary>Why a ticket is, or is not, part of the backfill.</summary>
public enum DefaultClassificationBackfillClass
{
    /// <summary>Missing, partial (request type only), dangling or inconsistent: gets Customer Services / General.</summary>
    Candidate,

    /// <summary>A valid classification: never touched.</summary>
    AlreadyValid,

    /// <summary>Already on the default: nothing to do.</summary>
    AlreadyDefault,

    /// <summary>A category with no request type, left alone unless <see cref="DefaultClassificationBackfillOptions.IncludeCategoryOnly"/> is on.</summary>
    CategoryOnlyKept,

    /// <summary>Belongs to Collections: out of scope, never touched.</summary>
    ExcludedCollections
}

public static class DefaultClassificationBackfillClassifier
{
    /// <summary>The single source of the decision - preview, dry run and live run all use it.</summary>
    public static DefaultClassificationBackfillClass Decide(
        TicketClassificationState state, bool isCollections, bool isAlreadyDefault, DefaultClassificationBackfillOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (isCollections) return DefaultClassificationBackfillClass.ExcludedCollections;
        if (isAlreadyDefault) return DefaultClassificationBackfillClass.AlreadyDefault;
        if (state == TicketClassificationState.Valid) return DefaultClassificationBackfillClass.AlreadyValid;
        if (state == TicketClassificationState.CategoryOnly && !options.IncludeCategoryOnly) return DefaultClassificationBackfillClass.CategoryOnlyKept;
        return DefaultClassificationBackfillClass.Candidate;
    }
}

public sealed record DefaultClassificationBackfillRow(
    long TicketId, string TicketNumber, TicketStatus Status, int DepartmentId, TicketClassificationState State,
    DefaultClassificationBackfillClass Class, Guid? OwnerEmployeeId, string Result);

/// <summary>A read-only look at what the backfill would do. Nothing is locked or written.</summary>
public sealed record DefaultClassificationBackfillPreview(
    bool ConfigurationAvailable, string? ConfigurationGap, int Examined, int Candidates,
    IReadOnlyDictionary<string, int> ByState, IReadOnlyDictionary<string, int> ByStatus, IReadOnlyDictionary<string, int> Exclusions,
    IReadOnlyList<DefaultClassificationBackfillRow> Rows, long NextAfterTicketId, bool Complete);

public enum DefaultClassificationBackfillRunStatus
{
    Completed,

    /// <summary>A LIVE run was requested but <c>DefaultClassificationBackfill:Enabled</c> is false. Nothing was done.</summary>
    Disabled,

    /// <summary>Customer Services / General is not configured. Nothing was done.</summary>
    ConfigurationMissing
}

public sealed record DefaultClassificationBackfillRunResult(
    DefaultClassificationBackfillRunStatus Status, bool DryRun, int Examined, int Applied, int WouldApply, int Skipped,
    IReadOnlyList<DefaultClassificationBackfillRow> Rows, long ResumeAfterTicketId, bool BacklogExhausted, string? Detail = null);

/// <summary>
/// Historical backfill of Customer Services / General onto existing tickets that have no valid classification - <b>never run automatically</b>.
/// The read-only <see cref="PreviewAsync"/> and every dry run are always allowed; a live run needs <c>DefaultClassificationBackfill:Enabled</c>.
/// It changes the category, request type and pinned workflow of a candidate ticket and nothing else: owner, department, priority, SLA, status
/// and history are untouched, a valid classification is never replaced, and Collections tickets are never candidates.
/// </summary>
public sealed class DefaultClassificationBackfillService(
    IDefaultClassificationBackfillRepository repository,
    DefaultClassificationService defaultClassification)
{
    public async Task<DefaultClassificationBackfillPreview> PreviewAsync(
        long afterTicketId, int limit, DefaultClassificationBackfillOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var take = Math.Clamp(limit < 1 ? options.DefaultBatchSize : limit, 1, Math.Max(1, options.MaxBatchSize));
        var resolution = await defaultClassification.ResolveAsync(cancellationToken);
        var tickets = await repository.ListPossiblyUnclassifiedAsync(afterTicketId, take, cancellationToken);

        var rows = new List<DefaultClassificationBackfillRow>();
        foreach (var ticket in tickets)
        {
            rows.Add(await JudgeAsync(ticket, options, resolution, "WouldApply", cancellationToken));
        }

        var candidates = rows.Where(r => r.Class == DefaultClassificationBackfillClass.Candidate).ToList();
        return new DefaultClassificationBackfillPreview(
            resolution.IsAvailable, resolution.Detail, rows.Count, candidates.Count,
            candidates.GroupBy(r => r.State.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            candidates.GroupBy(r => r.Status.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            rows.Where(r => r.Class != DefaultClassificationBackfillClass.Candidate).GroupBy(r => r.Class.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            rows, tickets.Count == 0 ? afterTicketId : tickets.Max(t => t.TicketId), tickets.Count < take);
    }

    /// <summary>One batch. <paramref name="dryRun"/> defaults to the safe choice at every caller; a live run is refused unless enabled.</summary>
    public async Task<DefaultClassificationBackfillRunResult> RunAsync(
        long afterTicketId, int limit, bool dryRun, DefaultClassificationBackfillOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!dryRun && !options.Enabled)
        {
            return new DefaultClassificationBackfillRunResult(
                DefaultClassificationBackfillRunStatus.Disabled, DryRun: false, 0, 0, 0, 0, [], afterTicketId, false,
                "Set DefaultClassificationBackfill:Enabled=true to run live. Dry runs are always allowed.");
        }

        var take = Math.Clamp(limit < 1 ? options.DefaultBatchSize : limit, 1, Math.Max(1, options.MaxBatchSize));
        var resolution = await defaultClassification.ResolveAsync(cancellationToken);
        if (!resolution.IsAvailable)
        {
            return new DefaultClassificationBackfillRunResult(
                DefaultClassificationBackfillRunStatus.ConfigurationMissing, dryRun, 0, 0, 0, 0, [], afterTicketId, false, resolution.Detail);
        }

        var tickets = await repository.ListPossiblyUnclassifiedAsync(afterTicketId, take, cancellationToken);
        var rows = new List<DefaultClassificationBackfillRow>();
        int applied = 0, would = 0, skipped = 0;
        foreach (var ticket in tickets)
        {
            var row = await JudgeAsync(ticket, options, resolution, dryRun ? "WouldApply" : "Applied", cancellationToken);
            if (row.Class != DefaultClassificationBackfillClass.Candidate)
            {
                skipped++;
            }
            else if (dryRun)
            {
                would++;
            }
            else
            {
                // Re-judged inside EnsureClassifiedAsync against fresh state; it writes the audit entry and saves this ticket alone.
                var outcome = await defaultClassification.EnsureClassifiedAsync(
                    ticket, "Historical backfill", actorEmployeeId: null, save: true, cancellationToken);
                if (outcome == DefaultClassificationApplyOutcome.Applied)
                {
                    applied++;
                }
                else
                {
                    skipped++;
                    row = row with { Result = $"Skipped:{outcome}" };
                }
            }

            rows.Add(row);
        }

        return new DefaultClassificationBackfillRunResult(
            DefaultClassificationBackfillRunStatus.Completed, dryRun, rows.Count, applied, would, skipped, rows,
            tickets.Count == 0 ? afterTicketId : tickets.Max(t => t.TicketId), tickets.Count < take);
    }

    private async Task<DefaultClassificationBackfillRow> JudgeAsync(
        Ticket ticket, DefaultClassificationBackfillOptions options, DefaultClassificationResolution resolution, string candidateResult,
        CancellationToken cancellationToken)
    {
        var state = await defaultClassification.AssessAsync(ticket.CategoryId, ticket.RequestTypeId, cancellationToken);
        var isDefault = resolution.Value is { } d && ticket.CategoryId == d.Category.CategoryId && ticket.RequestTypeId == d.RequestType.RequestTypeId;
        var klass = DefaultClassificationBackfillClassifier.Decide(
            state, await defaultClassification.IsCollectionsTicketAsync(ticket, cancellationToken), isDefault, options);
        return new DefaultClassificationBackfillRow(
            ticket.TicketId, ticket.TicketNumber, ticket.TicketStatus, ticket.CurrentDepartmentId, state, klass, ticket.CurrentOwnerEmployeeId,
            klass == DefaultClassificationBackfillClass.Candidate ? candidateResult : $"Skipped:{klass}");
    }
}
