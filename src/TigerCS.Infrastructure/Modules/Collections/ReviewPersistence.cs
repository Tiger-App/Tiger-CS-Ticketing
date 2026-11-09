using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Domain.Modules.Collections;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Collections;

public class CollectionsReviewRunConfiguration : IEntityTypeConfiguration<CollectionsReviewRun>
{
    public void Configure(EntityTypeBuilder<CollectionsReviewRun> builder)
    {
        builder.ToTable("CollectionsReviewRuns");
        builder.HasKey(r => r.CollectionsReviewRunId);
        builder.Property(r => r.CollectionsReviewRunId).ValueGeneratedOnAdd();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.Source).HasMaxLength(200).IsRequired();
        builder.Property(r => r.SourceProcedureSuffix).HasMaxLength(16).IsUnicode(false).IsRequired();
        builder.Property(r => r.Phase).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Error).HasMaxLength(500);
        // Single-flight refresh and a single current run, enforced by the database.
        builder.HasIndex(r => r.IsActive).IsUnique().HasFilter("[IsActive] = 1").HasDatabaseName("UX_CollectionsReviewRuns_Active");
        builder.HasIndex(r => r.IsCurrent).IsUnique().HasFilter("[IsCurrent] = 1").HasDatabaseName("UX_CollectionsReviewRuns_Current");
    }
}

public class CollectionsReviewRecordConfiguration : IEntityTypeConfiguration<CollectionsReviewRecord>
{
    public void Configure(EntityTypeBuilder<CollectionsReviewRecord> builder)
    {
        builder.ToTable("CollectionsReviewRecords");
        builder.HasKey(r => r.CollectionsReviewRecordId);
        builder.Property(r => r.CollectionsReviewRecordId).ValueGeneratedOnAdd();
        builder.Property(r => r.RecordKey).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(r => r.CycleKey).HasMaxLength(40).IsUnicode(false).IsRequired();
        builder.Property(r => r.ReminderType).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.TenantId).HasMaxLength(50).IsRequired();
        builder.Property(r => r.CustomerName).HasMaxLength(300).IsRequired();
        builder.Property(r => r.Phone).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(r => r.Email).HasMaxLength(320).IsRequired();
        builder.Property(r => r.UnitCode).HasMaxLength(60).IsRequired();
        builder.Property(r => r.ProjectCode).HasMaxLength(60).IsRequired();
        builder.Property(r => r.TowerNumber).HasMaxLength(20);
        builder.Property(r => r.TowerName).HasMaxLength(200);
        builder.Property(r => r.PaymentStatus).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.SourceStatus).HasMaxLength(100).IsRequired();
        builder.Property(r => r.RemainingAmount).HasPrecision(19, 4);
        builder.Property(r => r.RawRemainingAmount).HasPrecision(19, 4);
        builder.Property(r => r.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(r => r.ValidationStatus).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(r => r.Reasons).HasMaxLength(600).IsRequired();
        builder.Property(r => r.Source).HasMaxLength(200).IsRequired();
        builder.Property(r => r.InstalmentsJson);

        builder.HasIndex(r => new { r.CollectionsReviewRunId, r.RecordKey }).IsUnique().HasDatabaseName("UX_CollectionsReviewRecords_Run_Key");
        builder.HasIndex(r => new { r.CollectionsReviewRunId, r.ReminderType, r.ValidationStatus }).HasDatabaseName("IX_CollectionsReviewRecords_Run_Type_Status");
        builder.HasIndex(r => new { r.CollectionsReviewRunId, r.CompanyId, r.TenantId, r.UnitCode }).HasDatabaseName("IX_CollectionsReviewRecords_Run_Customer");
        builder.HasIndex(r => new { r.CollectionsReviewRunId, r.DueDate }).HasDatabaseName("IX_CollectionsReviewRecords_Run_Due");
        builder.HasOne<CollectionsReviewRun>().WithMany().HasForeignKey(r => r.CollectionsReviewRunId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class CollectionsDispatchConfiguration : IEntityTypeConfiguration<CollectionsDispatch>
{
    public void Configure(EntityTypeBuilder<CollectionsDispatch> builder)
    {
        builder.ToTable("CollectionsDispatches");
        builder.HasKey(d => d.CollectionsDispatchId);
        builder.Property(d => d.CollectionsDispatchId).ValueGeneratedOnAdd();
        builder.Property(d => d.IdempotencyKey).HasMaxLength(100).IsRequired();
        builder.Property(d => d.Fingerprint).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(d => d.SelectionMode).HasMaxLength(24).IsRequired();
        builder.Property(d => d.FilterJson).HasMaxLength(2000).IsRequired();
        builder.Property(d => d.ApprovedTotalsJson).HasMaxLength(500).IsRequired();
        builder.Property(d => d.StatusReason).HasMaxLength(500);
        builder.Property(d => d.Phase).HasMaxLength(200);
        builder.Property(d => d.AdminEmailStatus).HasConversion<string>().HasMaxLength(16).IsRequired().HasDefaultValue(AdminEmailStatus.None);
        builder.Property(d => d.AdminEmailError).HasMaxLength(500);
        builder.HasIndex(d => d.IdempotencyKey).IsUnique().HasDatabaseName("UX_CollectionsDispatches_IdempotencyKey");
        builder.HasIndex(d => d.PublicId).IsUnique().HasDatabaseName("UX_CollectionsDispatches_PublicId");
        builder.HasMany(d => d.Items).WithOne().HasForeignKey(i => i.CollectionsDispatchId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(d => d.Batches).WithOne().HasForeignKey(b => b.CollectionsDispatchId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class CollectionsDispatchItemConfiguration : IEntityTypeConfiguration<CollectionsDispatchItem>
{
    public const string LiveFilter = "[Status] IN ('Approved','UploadedToGenesys','UnknownOutcome')";

    public void Configure(EntityTypeBuilder<CollectionsDispatchItem> builder)
    {
        builder.ToTable("CollectionsDispatchItems");
        builder.HasKey(i => i.CollectionsDispatchItemId);
        builder.Property(i => i.CollectionsDispatchItemId).ValueGeneratedOnAdd();
        builder.Property(i => i.RecordKey).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(i => i.ReminderType).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(i => i.TenantId).HasMaxLength(50).IsRequired();
        builder.Property(i => i.UnitCode).HasMaxLength(60).IsRequired();
        builder.Property(i => i.ProjectName).HasMaxLength(200).IsRequired().HasDefaultValue("");
        builder.Property(i => i.TowerName).HasMaxLength(200).IsRequired().HasDefaultValue("");
        builder.Property(i => i.CustomerName).HasMaxLength(300).IsRequired();
        builder.Property(i => i.Phone).HasMaxLength(20).IsUnicode(false).IsRequired();
        builder.Property(i => i.Email).HasMaxLength(320).IsRequired();
        builder.Property(i => i.Amount).HasPrecision(19, 4);
        builder.Property(i => i.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(i => i.StatusReason).HasMaxLength(500);
        builder.Property(i => i.GenesysContactId).HasMaxLength(64).IsUnicode(false);
        builder.Property(i => i.SuppressionStatus).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(i => i.SuppressionError).HasMaxLength(500);
        // The database guarantee behind duplicate prevention: a record can be in only one live dispatch
        // (approved, uploaded or of unknown outcome) at a time, whatever the idempotency key.
        builder.HasIndex(i => i.RecordKey, "UX_CollectionsDispatchItems_LiveRecord").IsUnique().HasFilter(LiveFilter);
        builder.HasIndex(i => i.RecordKey, "IX_CollectionsDispatchItems_RecordKey");
        builder.HasIndex(i => i.CollectionsGenesysBatchId).HasDatabaseName("IX_CollectionsDispatchItems_Batch");
    }
}

public class CollectionsGenesysBatchConfiguration : IEntityTypeConfiguration<CollectionsGenesysBatch>
{
    public void Configure(EntityTypeBuilder<CollectionsGenesysBatch> builder)
    {
        builder.ToTable("CollectionsGenesysBatches");
        builder.HasKey(b => b.CollectionsGenesysBatchId);
        builder.Property(b => b.CollectionsGenesysBatchId).ValueGeneratedOnAdd();
        builder.Property(b => b.ReminderType).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(b => b.ContactListId).HasMaxLength(36).IsUnicode(false).IsRequired();
        builder.Property(b => b.Status).HasConversion<string>().HasMaxLength(24).IsRequired();
        builder.Property(b => b.StageName).HasMaxLength(200);
        builder.Property(b => b.GenesysCampaignName).HasMaxLength(200);
        builder.Property(b => b.GenesysCampaignId).HasMaxLength(64).IsUnicode(false);
        builder.Property(b => b.CampaignAction).HasMaxLength(24);
        builder.Property(b => b.RequestHash).HasMaxLength(64).IsUnicode(false);
        builder.Property(b => b.Error).HasMaxLength(500);
        builder.Property(b => b.ReconciliationNote).HasMaxLength(500);
        builder.HasIndex(b => new { b.CollectionsDispatchId, b.Sequence }).IsUnique().HasDatabaseName("UX_CollectionsGenesysBatches_Dispatch_Sequence");
    }
}

public class CollectionsGenesysMessageResultConfiguration : IEntityTypeConfiguration<CollectionsGenesysMessageResult>
{
    public void Configure(EntityTypeBuilder<CollectionsGenesysMessageResult> builder)
    {
        builder.ToTable("CollectionsGenesysMessageResults");
        builder.HasKey(r => r.CollectionsGenesysMessageResultId);
        builder.Property(r => r.CollectionsGenesysMessageResultId).ValueGeneratedOnAdd();
        builder.Property(r => r.GenesysConversationId).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(r => r.GenesysMessageId).HasMaxLength(128).IsUnicode(false).IsRequired();
        builder.Property(r => r.GenesysContactId).HasMaxLength(64).IsUnicode(false);
        builder.Property(r => r.Status).HasMaxLength(16).IsUnicode(false).IsRequired();
        builder.Property(r => r.FailureReason).HasMaxLength(300);
        // One row per Genesys message, whatever the number of syncs or delivery events.
        builder.HasIndex(r => new { r.GenesysConversationId, r.GenesysMessageId }).IsUnique().HasDatabaseName("UX_CollectionsGenesysMessageResults_Message");
        builder.HasIndex(r => new { r.CollectionsDispatchId, r.CollectionsDispatchItemId }).HasDatabaseName("IX_CollectionsGenesysMessageResults_Dispatch_Item");
    }
}

public class CollectionsGenesysSyncStateConfiguration : IEntityTypeConfiguration<CollectionsGenesysSyncState>
{
    public void Configure(EntityTypeBuilder<CollectionsGenesysSyncState> builder)
    {
        builder.ToTable("CollectionsGenesysSyncStates");
        builder.HasKey(s => s.CollectionsDispatchId);
        builder.Property(s => s.CollectionsDispatchId).ValueGeneratedNever();
        builder.Property(s => s.Outcome).HasMaxLength(16).IsUnicode(false).IsRequired();
        builder.Property(s => s.Error).HasMaxLength(500);
    }
}

/// <summary>
/// Which database failures are worth a bounded retry, and how long to wait. Only connection-level failures (a dropped or timed-out link, a failed-over server, a deadlock victim) qualify;
/// a constraint violation or any logic error is never retried. Callers retry ONLY writes that are safe to repeat.
/// </summary>
public sealed class ReviewWriteRetryPolicy
{
    private static readonly HashSet<int> TransientNumbers =
    [
        -1,      // connection could not be established / transport error (the 10054 / 10060 family)
        -2,      // command or connection timeout
        20, 64, 233, 10053, 10054, 10060, 10061, 258,   // transport / network / "wait operation timed out"
        121,     // semaphore timeout
        1205,    // chosen as deadlock victim: the transaction was rolled back
        4060, 40197, 40501, 40613, 49918, 49919, 49920, 40143, 10928, 10929, 10936,   // failover / throttling / resource limits (the numbers EF's SqlServer detector also treats as transient)
        41301, 41302, 41305, 41325, 41839
    ];

    public int MaxAttempts { get; init; } = 3;
    public Func<int, TimeSpan> Delay { get; init; } = attempt => TimeSpan.FromSeconds(2 * attempt);
    /// <summary>Extra classification (tests, providers other than SQL Server).</summary>
    public Func<Exception, bool>? ExtraTransient { get; init; }

    public bool IsTransient(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (ExtraTransient?.Invoke(e) == true) return true;
            if (e is TimeoutException) return true;
            if (e is SqlException sql && sql.Errors.Count > 0 && sql.Errors.Cast<SqlError>().Any(x => TransientNumbers.Contains(x.Number))) return true;
            if (e is SqlException { Number: var n } && TransientNumbers.Contains(n)) return true;
            if (e.InnerException is null) break;
        }
        return false;
    }

    public static int? NumberOf(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            if (e is SqlException sql) return sql.Number;
            if (e.InnerException is null) break;
        }
        return null;
    }
}

public sealed class ReviewStore(TigerCsDbContext db, ReviewWriteRetryPolicy? retryPolicy = null) : IReviewStore, IGenesysExportHistory
{
    private readonly ReviewWriteRetryPolicy _retry = retryPolicy ?? new ReviewWriteRetryPolicy();

    private static readonly DispatchItemStatus[] LiveStatuses = [DispatchItemStatus.Approved, DispatchItemStatus.UploadedToGenesys, DispatchItemStatus.UnknownOutcome];

    // ---------------------------------------------------------------- earlier file exports (audit trail, read only)

    public async Task<IReadOnlyList<GenesysExportAuditRecord>> ListExportsAsync(string action, DateTime sinceUtc, int take, CancellationToken ct) =>
        (await db.AuditEntries.AsNoTracking().Where(a => a.Action == action && a.OccurredAtUtc >= sinceUtc)
            .OrderByDescending(a => a.OccurredAtUtc).Take(take)
            .Select(a => new { a.OccurredAtUtc, a.ActorEmployeeId, a.AfterValue }).ToListAsync(ct))
        .Select(a => new GenesysExportAuditRecord(a.OccurredAtUtc, a.ActorEmployeeId, a.AfterValue)).ToList();

    // ---------------------------------------------------------------- runs

    public Task<CollectionsReviewRun?> GetCurrentRunAsync(CancellationToken ct) =>
        RunWithRetryAsync("read the current review run", token => db.CollectionsReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.IsCurrent, token), ct);

    public Task<CollectionsReviewRun?> GetRunAsync(long runId, CancellationToken ct) =>
        RunWithRetryAsync("read the review run", token => db.CollectionsReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.CollectionsReviewRunId == runId, token), ct);

    public Task<CollectionsReviewRun?> GetLatestRunAsync(CancellationToken ct) =>
        RunWithRetryAsync("read the latest review run", token => db.CollectionsReviewRuns.AsNoTracking().OrderByDescending(r => r.CollectionsReviewRunId).FirstOrDefaultAsync(token), ct);

    public Task<CollectionsReviewRun?> GetActiveRunAsync(CancellationToken ct) =>
        RunWithRetryAsync("read the active review run", token => db.CollectionsReviewRuns.AsNoTracking().FirstOrDefaultAsync(r => r.IsActive, token), ct);

    public async Task<bool> TryAddRunAsync(CollectionsReviewRun run, CancellationToken ct)
    {
        run.IsActive = true;
        db.CollectionsReviewRuns.Add(run);
        try { await db.SaveChangesAsync(ct); db.Entry(run).State = EntityState.Detached; return true; }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); return false; }
    }

    /// <summary>
    /// Queued to Running is a compare-and-set, so it is NOT retried blindly: if the first attempt's answer is lost after the row really changed, a second attempt would report "already
    /// claimed" and the run would be abandoned while Running. A transient failure is reported as a clear <see cref="ReviewStorageException"/> instead, and the caller fails the run visibly.
    /// </summary>
    public async Task<bool> TryStartRunAsync(long runId, DateTime nowUtc, CancellationToken ct)
    {
        try
        {
            return await db.CollectionsReviewRuns.Where(r => r.CollectionsReviewRunId == runId && r.Status == ReviewRunStatus.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReviewRunStatus.Running).SetProperty(r => r.StartedAtUtc, nowUtc)
                    .SetProperty(r => r.Phase, "Starting").SetProperty(r => r.ProgressPercent, 1), ct) == 1;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OperationCanceledException && _retry.IsTransient(ex))
        {
            db.ChangeTracker.Clear();
            await ResetConnectionAsync();
            throw TransientFailure("start the review refresh", 1, ex);
        }
    }

    /// <summary>Sets absolute values, so repeating it after an ambiguous failure is harmless: it is retried like any other idempotent write.</summary>
    public Task UpdateRunProgressAsync(long runId, string phase, int percent, int? sourceRows, CancellationToken ct) =>
        WriteWithRetryAsync("update the review refresh progress", token => db.CollectionsReviewRuns.Where(r => r.CollectionsReviewRunId == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Phase, phase).SetProperty(r => r.ProgressPercent, percent)
                .SetProperty(r => r.SourceRowCount, r => sourceRows ?? r.SourceRowCount), token), ct);

    public Task SetRunSourceReconciledAsync(long runId, bool reconciled, CancellationToken ct) =>
        WriteWithRetryAsync("record the reconciliation state on the review refresh", token => db.CollectionsReviewRuns.Where(r => r.CollectionsReviewRunId == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.SourceReconciled, reconciled), token), ct);

    private static ReviewStorageException TransientFailure(string what, int attempts, Exception ex) => new(
        $"Could not {what}: the database connection was lost or timed out after {attempts} attempt(s)" + (ReviewWriteRetryPolicy.NumberOf(ex) is { } n ? $" (SQL error {n})" : "")
        + ". Nothing was published; the previous review data is still in use. Retry.", true, ReviewWriteRetryPolicy.NumberOf(ex), ex);

    /// <summary>A bounded retry for an idempotent READ or write that returns a value (same rules as <see cref="WriteWithRetryAsync"/>).</summary>
    private async Task<T> RunWithRetryAsync<T>(string what, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        T? result = default;
        await WriteWithRetryAsync(what, async token => { result = await operation(token); }, ct);
        return result!;
    }

    /// <summary>
    /// Runs an IDEMPOTENT write, retrying a bounded number of times on a transient connection failure with a fresh connection and a clean change tracker.
    /// The result of a lost connection can be ambiguous (the commit may have happened), which is why only writes that converge on the same state when repeated are passed here.
    /// </summary>
    private async Task WriteWithRetryAsync(string what, Func<CancellationToken, Task> write, CancellationToken ct)
    {
        var attempts = Math.Clamp(_retry.MaxAttempts, 1, 5);
        for (var attempt = 1; ; attempt++)
        {
            try { await write(ct); return; }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OperationCanceledException && _retry.IsTransient(ex))
            {
                db.ChangeTracker.Clear();
                await ResetConnectionAsync();
                if (attempt >= attempts) throw TransientFailure(what, attempt, ex);
                await Task.Delay(_retry.Delay(attempt), ct);
            }
        }
    }

    /// <summary>Drops the (possibly dead) connection and its pool siblings so the next attempt opens a new one.</summary>
    private async Task ResetConnectionAsync()
    {
        try
        {
            var connection = db.Database.GetDbConnection();
            await db.Database.CloseConnectionAsync();
            if (connection is SqlConnection sql) SqlConnection.ClearPool(sql);
        }
        catch (Exception) { /* the next attempt reports the real problem */ }
    }

    public Task FailRunAsync(long runId, string error, DateTime nowUtc, CancellationToken ct) =>
        WriteWithRetryAsync("record the failure of the review refresh", async token =>
        {
            await db.CollectionsReviewRecords.Where(r => r.CollectionsReviewRunId == runId).ExecuteDeleteAsync(token);
            await db.CollectionsReviewRuns.Where(r => r.CollectionsReviewRunId == runId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReviewRunStatus.Failed).SetProperty(r => r.IsActive, false)
                    .SetProperty(r => r.Error, error.Length > 500 ? error[..500] : error).SetProperty(r => r.CompletedAtUtc, nowUtc)
                    .SetProperty(r => r.Phase, "Failed"), token);
        }, ct);

    /// <summary>
    /// Writes one batch of records as ONE transaction that first removes any rows of the same keys for this run, then inserts them. Repeating it (after a lost connection whose
    /// commit may or may not have happened) therefore converges on exactly the batch's rows: nothing duplicated (the unique run+key index would refuse that anyway), nothing skipped.
    /// </summary>
    public Task AddRecordsAsync(long runId, IReadOnlyList<CollectionsReviewRecord> records, CancellationToken ct)
    {
        if (records.Count == 0) return Task.CompletedTask;
        var keys = records.Select(r => r.RecordKey).ToList();
        return WriteWithRetryAsync("save the review records", async token =>
        {
            foreach (var record in records) { record.CollectionsReviewRunId = runId; record.CollectionsReviewRecordId = 0; }
            await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
            await db.CollectionsReviewRecords.Where(r => r.CollectionsReviewRunId == runId && keys.Contains(r.RecordKey)).ExecuteDeleteAsync(token);
            db.CollectionsReviewRecords.AddRange(records);
            await db.SaveChangesAsync(token);
            if (tx is not null) await tx.CommitAsync(token);
            db.ChangeTracker.Clear();
        }, ct);
    }

    /// <summary>All set-based and ending in the same state however often it runs, so a repeat after an ambiguous commit is safe.</summary>
    public Task PublishRunAsync(long runId, int recordCount, int sourceRows, DateTime sourceReadAtUtc, DateTime nowUtc, int runsToKeep, CancellationToken ct) =>
        WriteWithRetryAsync("publish the review data", async token =>
        {
            // A relational transaction can be unavailable on the InMemory provider used by some tests; the unique "current" index
            // is what keeps two published runs from coexisting on a real database.
            await using var tx = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(token) : null;
            await db.CollectionsReviewRuns.Where(r => r.IsCurrent && r.CollectionsReviewRunId != runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.IsCurrent, false), token);
            await db.CollectionsReviewRuns.Where(r => r.CollectionsReviewRunId == runId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReviewRunStatus.Completed).SetProperty(r => r.IsCurrent, true)
                    .SetProperty(r => r.IsActive, false).SetProperty(r => r.CompletedAtUtc, nowUtc).SetProperty(r => r.SourceReadAtUtc, sourceReadAtUtc)
                    .SetProperty(r => r.RecordCount, recordCount).SetProperty(r => r.SourceRowCount, sourceRows)
                    .SetProperty(r => r.ProgressPercent, 100).SetProperty(r => r.Phase, "Completed"), token);
            var keep = await db.CollectionsReviewRuns.Where(r => r.Status == ReviewRunStatus.Completed)
                .OrderByDescending(r => r.CollectionsReviewRunId).Select(r => r.CollectionsReviewRunId).Take(Math.Max(1, runsToKeep)).ToListAsync(token);
            await db.CollectionsReviewRecords.Where(r => !keep.Contains(r.CollectionsReviewRunId)
                && db.CollectionsReviewRuns.Any(x => x.CollectionsReviewRunId == r.CollectionsReviewRunId && x.Status != ReviewRunStatus.Running && x.Status != ReviewRunStatus.Queued))
                .ExecuteDeleteAsync(token);
            if (tx is not null) await tx.CommitAsync(token);
        }, ct);

    // ---------------------------------------------------------------- review queries

    private sealed class Row
    {
        public CollectionsReviewRecord Record { get; set; } = null!;
        public bool Sent { get; set; }
    }

    /// <summary>
    /// The unit codes of the run that sit on the chosen floors, or null when no floor is chosen (All floors). The floor is derived from the unit code (<see cref="CollectionsUnitFloor"/>; the review records
    /// carry no floor column and none is added), so it cannot be a SQL predicate: the DISTINCT unit codes of the run (narrowed by company and tower first) are classified once and the main query then keeps
    /// the records of exactly those codes. One cheap projection of strings, never a read of whole records.
    /// </summary>
    private async Task<IReadOnlyCollection<string>?> FloorUnitCodesAsync(long runId, ReviewQuerySpec s, CancellationToken ct)
    {
        if (s.Floors is not { Count: > 0 } floors) return null;
        var wanted = floors.ToHashSet();
        var q = db.CollectionsReviewRecords.AsNoTracking().Where(r => r.CollectionsReviewRunId == runId);
        if (s.CompanyId is { } company) q = q.Where(r => r.CompanyId == company);
        if (s.TowerId is { } tower) q = q.Where(r => r.TowerId == tower);
        if (s.TowerIds is { Count: > 0 } towers) q = q.Where(r => r.TowerId != null && towers.Contains(r.TowerId.Value));
        var codes = await q.Select(r => r.UnitCode).Distinct().ToListAsync(ct);
        return codes.Where(code => CollectionsUnitFloor.Matches(code, wanted)).ToList();
    }

    private IQueryable<CollectionsReviewRecord> Filtered(long runId, ReviewQuerySpec s, IReadOnlyCollection<string>? floorUnitCodes = null)
    {
        var q = db.CollectionsReviewRecords.AsNoTracking().Where(r => r.CollectionsReviewRunId == runId);
        if (s.CompanyId is { } company) q = q.Where(r => r.CompanyId == company);
        if (s.Project is { } project) { var p = Like(project); q = q.Where(r => EF.Functions.Like(r.ProjectCode, p, "\\")); }
        if (s.TowerId is { } tower) q = q.Where(r => r.TowerId == tower);
        if (s.TowerIds is { Count: > 0 } towers) q = q.Where(r => r.TowerId != null && towers.Contains(r.TowerId.Value));
        if (floorUnitCodes is not null) q = q.Where(r => floorUnitCodes.Contains(r.UnitCode));   // empty = no unit on the chosen floors = nobody
        if (s.Unit is { } unit) { var p = Like(unit); q = q.Where(r => EF.Functions.Like(r.UnitCode, p, "\\")); }
        if (s.CustomerText is { } text)
        {
            // The same fields the Campaigns preview searches: name, customer id, unit code, e-mail, tower number or name, and the phone's digits.
            var name = Like(text);
            var digits = s.PhoneDigits is null ? null : Like(s.PhoneDigits);
            q = q.Where(r => EF.Functions.Like(r.CustomerName, name, "\\") || EF.Functions.Like(r.TenantId, name, "\\") || EF.Functions.Like(r.UnitCode, name, "\\")
                || EF.Functions.Like(r.Email, name, "\\") || (r.TowerNumber != null && EF.Functions.Like(r.TowerNumber, name, "\\")) || (r.TowerName != null && EF.Functions.Like(r.TowerName, name, "\\"))
                || (digits != null && EF.Functions.Like(r.Phone, digits, "\\")));
        }
        // Special case units are never part of a campaign list, whatever the stored run still holds (the exclusion also applies to data stored before it was configured).
        if (s.ExcludedUnitCodes is { Count: > 0 } excluded) q = q.Where(r => !excluded.Contains(r.UnitCode.Trim().ToUpper()));
        if (s.DueFrom is { } from) q = q.Where(r => r.DueDate >= from);
        if (s.DueTo is { } to) q = q.Where(r => r.DueDate <= to);
        if (s.PaymentStatuses is { } statuses) q = q.Where(r => statuses.Contains(r.PaymentStatus));
        if (s.MinRemaining is { } min) q = q.Where(r => r.RemainingAmount >= min);
        if (s.MaxRemaining is { } max) q = q.Where(r => r.RemainingAmount <= max);
        if (s.ReminderType is { } type) q = q.Where(r => r.ReminderType == type);
        if (s.Reason is { } reason) { var p = $"%;{reason};%"; q = q.Where(r => EF.Functions.Like(r.Reasons, p)); }
        return q;
    }

    /// <summary>
    /// "Sent" = the record is in a live or uploaded dispatch. When <paramref name="contactedSinceUtc"/> is given (Manual and Auto dispatch) the contact-frequency limit adds: the same customer is in a
    /// live or uploaded dispatch of ANY record that was approved since then (the start of today), so a customer is never added to a Genesys list twice in a day.
    /// </summary>
    private IQueryable<Row> WithSent(IQueryable<CollectionsReviewRecord> q, DateTime? contactedSinceUtc = null)
    {
        var limit = contactedSinceUtc is not null;
        var since = contactedSinceUtc ?? DateTime.MaxValue;
        return q.Select(r => new Row
        {
            Record = r,
            Sent = db.CollectionsDispatchItems.Any(i => i.RecordKey == r.RecordKey && LiveStatuses.Contains(i.Status))
                || (limit
                    && db.CollectionsDispatchItems.Any(i => i.CompanyId == r.CompanyId && i.TenantId == r.TenantId && i.UnitCode == r.UnitCode && LiveStatuses.Contains(i.Status)
                        && db.CollectionsDispatches.Any(d => d.CollectionsDispatchId == i.CollectionsDispatchId && d.InitiatedAtUtc >= since)))
        });
    }

    private static IQueryable<Row> ByStatus(IQueryable<Row> q, ReviewValidationStatus? status, bool stale) => status switch
    {
        null => q,
        ReviewValidationStatus.AlreadySent => q.Where(x => x.Sent),
        ReviewValidationStatus.Ready => stale ? q.Where(_ => false) : q.Where(x => !x.Sent && x.Record.ValidationStatus == ReviewValidationStatus.Ready),
        ReviewValidationStatus.NeedsReview => q.Where(x => !x.Sent && (x.Record.ValidationStatus == ReviewValidationStatus.NeedsReview
            || (stale && x.Record.ValidationStatus == ReviewValidationStatus.Ready))),
        _ => q.Where(x => !x.Sent && x.Record.ValidationStatus == status)
    };

    public async Task<StoredPage> QueryAsync(long runId, ReviewQuerySpec spec, bool stale, int skip, int take, CancellationToken ct)
    {
        var q = ByStatus(WithSent(Filtered(runId, spec, await FloorUnitCodesAsync(runId, spec, ct)), spec.ContactedSinceUtc), spec.ValidationStatus, stale);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(x => x.Record.CompanyId).ThenBy(x => x.Record.TenantId).ThenBy(x => x.Record.UnitCode)
            .ThenBy(x => x.Record.ReminderType).ThenBy(x => x.Record.CollectionsReviewRecordId).Skip(skip).Take(take).ToListAsync(ct);
        return new StoredPage(await WithHistoryAsync(rows, ct), total);
    }

    public Task<bool> RunHasTowerDataAsync(long runId, CancellationToken ct) =>
        db.CollectionsReviewRecords.AsNoTracking().AnyAsync(r => r.CollectionsReviewRunId == runId && r.TowerId != null, ct);

    public async Task<ReviewCountsDto> CountAsync(long runId, ReviewQuerySpec specWithoutStatus, bool stale, CancellationToken ct)
    {
        var baseQuery = WithSent(Filtered(runId, specWithoutStatus with { ValidationStatus = null }, await FloorUnitCodesAsync(runId, specWithoutStatus, ct)), specWithoutStatus.ContactedSinceUtc);
        var ready = await ByStatus(baseQuery, ReviewValidationStatus.Ready, stale).CountAsync(ct);
        var review = await ByStatus(baseQuery, ReviewValidationStatus.NeedsReview, stale).CountAsync(ct);
        var excluded = await ByStatus(baseQuery, ReviewValidationStatus.Excluded, stale).CountAsync(ct);
        var sent = await ByStatus(baseQuery, ReviewValidationStatus.AlreadySent, stale).CountAsync(ct);
        return new ReviewCountsDto(ready + review + excluded + sent, ready, review, excluded, sent);
    }

    public async Task<IReadOnlyList<ReviewRecordView>> ResolveAsync(long runId, ReviewQuerySpec spec, IReadOnlyCollection<string>? keys, bool stale, int max, CancellationToken ct,
        IReadOnlyCollection<string>? unitKeys = null)
    {
        var q = Filtered(runId, spec, await FloorUnitCodesAsync(runId, spec, ct));
        if (keys is not null) q = q.Where(r => keys.Contains(r.RecordKey));
        if (unitKeys is not null) q = q.Where(r => unitKeys.Contains(r.CompanyId.ToString() + "|" + r.TenantId + "|" + r.UnitCode));
        var rows = await ByStatus(WithSent(q, spec.ContactedSinceUtc), spec.ValidationStatus, stale).OrderBy(x => x.Record.RecordKey).Take(max + 1).ToListAsync(ct);
        return await WithHistoryAsync(rows, ct);
    }

    private async Task<IReadOnlyList<ReviewRecordView>> WithHistoryAsync(List<Row> rows, CancellationToken ct)
    {
        var keys = rows.Select(r => r.Record.RecordKey).Distinct().ToList();
        var history = keys.Count == 0 ? [] : await (from i in db.CollectionsDispatchItems.AsNoTracking()
                                                    join d in db.CollectionsDispatches.AsNoTracking() on i.CollectionsDispatchId equals d.CollectionsDispatchId
                                                    where keys.Contains(i.RecordKey)
                                                    orderby i.CollectionsDispatchItemId descending
                                                    select new { i.RecordKey, i.Status, At = i.UploadedAtUtc ?? d.InitiatedAtUtc }).ToListAsync(ct);
        var latest = history.GroupBy(h => h.RecordKey).ToDictionary(g => g.Key, g => g.First());
        return rows.Select(r => latest.TryGetValue(r.Record.RecordKey, out var h)
            ? new ReviewRecordView(r.Record, r.Sent, h.Status.ToString(), h.At)
            : new ReviewRecordView(r.Record, r.Sent, null, null)).ToList();
    }

    private static string Like(string value) =>
        "%" + value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[") + "%";

    // ---------------------------------------------------------------- dispatch

    public Task<CollectionsDispatch?> FindDispatchByKeyAsync(string idempotencyKey, CancellationToken ct) =>
        Dispatches().FirstOrDefaultAsync(d => d.IdempotencyKey == idempotencyKey, ct);

    private IQueryable<CollectionsDispatch> Dispatches() =>
        db.CollectionsDispatches.AsNoTracking().Include(d => d.Items).Include(d => d.Batches).AsSplitQuery();

    public async Task<bool> TryAddDispatchAsync(CollectionsDispatch dispatch, CancellationToken ct)
    {
        db.CollectionsDispatches.Add(dispatch);
        try { await db.SaveChangesAsync(ct); db.ChangeTracker.Clear(); return true; }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { db.ChangeTracker.Clear(); return false; }
    }

    public Task<CollectionsDispatch?> GetDispatchAsync(Guid publicId, CancellationToken ct) =>
        Dispatches().FirstOrDefaultAsync(d => d.PublicId == publicId, ct);

    public Task<CollectionsDispatch?> GetDispatchByIdAsync(long dispatchId, CancellationToken ct) =>
        Dispatches().FirstOrDefaultAsync(d => d.CollectionsDispatchId == dispatchId, ct);

    public async Task<IReadOnlyList<CollectionsDispatch>> ListDispatchesAsync(int take, CancellationToken ct) =>
        await Dispatches().OrderByDescending(d => d.CollectionsDispatchId).Take(take).ToListAsync(ct);

    public async Task<bool> TryAcquireLeaseAsync(long dispatchId, Guid owner, DateTime nowUtc, TimeSpan ttl, CancellationToken ct)
    {
        var expires = nowUtc + ttl;
        return await db.CollectionsDispatches.Where(d => d.CollectionsDispatchId == dispatchId
                && (d.Status == DispatchStatus.Queued || d.Status == DispatchStatus.Revalidating || d.Status == DispatchStatus.Sending)
                && (d.LeaseExpiresAtUtc == null || d.LeaseExpiresAtUtc < nowUtc))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LeaseOwner, owner).SetProperty(d => d.LeaseExpiresAtUtc, expires)
                .SetProperty(d => d.Status, d => d.Status == DispatchStatus.Queued ? DispatchStatus.Revalidating : d.Status), ct) == 1;
    }

    public async Task<bool> TryClaimAdminEmailAsync(long dispatchId, DateTime nowUtc, DateTime staleBeforeUtc, CancellationToken ct) =>
        await db.CollectionsDispatches.Where(d => d.CollectionsDispatchId == dispatchId
                && (d.Status == DispatchStatus.Completed || d.Status == DispatchStatus.CompletedWithErrors)
                && (d.AdminEmailStatus == AdminEmailStatus.None || d.AdminEmailStatus == AdminEmailStatus.Failed || d.AdminEmailStatus == AdminEmailStatus.NotConfigured
                    || d.AdminEmailStatus == AdminEmailStatus.Disabled
                    || (d.AdminEmailStatus == AdminEmailStatus.Sending && d.AdminEmailAttemptedAtUtc < staleBeforeUtc)))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.AdminEmailStatus, AdminEmailStatus.Sending).SetProperty(d => d.AdminEmailAttemptedAtUtc, nowUtc)
                .SetProperty(d => d.AdminEmailAttempts, d => d.AdminEmailAttempts + 1), ct) == 1;

    public Task RecordAdminEmailAsync(long dispatchId, AdminEmailStatus status, string? error, int recipientCount, DateTime nowUtc, CancellationToken ct) =>
        db.CollectionsDispatches.Where(d => d.CollectionsDispatchId == dispatchId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.AdminEmailStatus, status).SetProperty(d => d.AdminEmailError, error == null ? null : (error.Length > 500 ? error.Substring(0, 500) : error))
                .SetProperty(d => d.AdminEmailRecipientCount, recipientCount).SetProperty(d => d.AdminEmailAttemptedAtUtc, nowUtc), ct);

    public async Task<bool> RenewLeaseAsync(long dispatchId, Guid owner, DateTime nowUtc, TimeSpan ttl, CancellationToken ct)
    {
        var expires = nowUtc + ttl;
        return await db.CollectionsDispatches.Where(d => d.CollectionsDispatchId == dispatchId && d.LeaseOwner == owner)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LeaseExpiresAtUtc, expires), ct) == 1;
    }

    public async Task<bool> TryCancelQueuedAsync(long dispatchId, string reason, DateTime nowUtc, CancellationToken ct) =>
        await db.CollectionsDispatches.Where(d => d.CollectionsDispatchId == dispatchId && d.Status == DispatchStatus.Queued && d.LeaseOwner == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DispatchStatus.Cancelled).SetProperty(d => d.StatusReason, reason)
                .SetProperty(d => d.CompletedAtUtc, nowUtc), ct) == 1;

    public async Task UpdateDispatchAsync(CollectionsDispatch dispatch, CancellationToken ct)
    {
        var tracked = await db.CollectionsDispatches.FirstAsync(d => d.CollectionsDispatchId == dispatch.CollectionsDispatchId, ct);
        tracked.Status = dispatch.Status; tracked.StatusReason = Cut(dispatch.StatusReason); tracked.StartedAtUtc = dispatch.StartedAtUtc;
        tracked.CompletedAtUtc = dispatch.CompletedAtUtc; tracked.ExcludedAtDispatchCount = dispatch.ExcludedAtDispatchCount;
        tracked.Phase = dispatch.Phase;
        if (dispatch.LeaseOwner is null) { tracked.LeaseOwner = null; tracked.LeaseExpiresAtUtc = null; }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    public Task ReleaseItemsAsync(long dispatchId, string reason, CancellationToken ct)
    {
        var text = Cut(reason);
        return db.CollectionsDispatchItems.Where(i => i.CollectionsDispatchId == dispatchId && i.Status == DispatchItemStatus.Approved)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, DispatchItemStatus.Released).SetProperty(i => i.StatusReason, text), ct);
    }

    public async Task AddBatchesAsync(IReadOnlyList<CollectionsGenesysBatch> batches, CancellationToken ct)
    {
        db.CollectionsGenesysBatches.AddRange(batches);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    public Task<CollectionsGenesysBatch?> GetBatchAsync(long batchId, CancellationToken ct) =>
        db.CollectionsGenesysBatches.AsNoTracking().FirstOrDefaultAsync(b => b.CollectionsGenesysBatchId == batchId, ct);

    public async Task<bool> TryClaimBatchAsync(long batchId, DateTime nowUtc, CancellationToken ct) =>
        await db.CollectionsGenesysBatches.Where(b => b.CollectionsGenesysBatchId == batchId && b.Status == GenesysBatchStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, GenesysBatchStatus.Submitting)
                .SetProperty(b => b.AttemptCount, b => b.AttemptCount + 1).SetProperty(b => b.StartedAtUtc, nowUtc), ct) == 1;

    public async Task UpdateBatchAsync(CollectionsGenesysBatch batch, CancellationToken ct)
    {
        var tracked = await db.CollectionsGenesysBatches.FirstAsync(b => b.CollectionsGenesysBatchId == batch.CollectionsGenesysBatchId, ct);
        tracked.Status = batch.Status; tracked.Error = Cut(batch.Error); tracked.HttpStatus = batch.HttpStatus;
        tracked.ReturnedContactCount = batch.ReturnedContactCount; tracked.RequestHash = batch.RequestHash;
        tracked.CompletedAtUtc = batch.CompletedAtUtc; tracked.ReconciledByEmployeeId = batch.ReconciledByEmployeeId;
        tracked.ReconciledAtUtc = batch.ReconciledAtUtc; tracked.ReconciliationNote = Cut(batch.ReconciliationNote);
        tracked.StageName = batch.StageName; tracked.GenesysCampaignName = batch.GenesysCampaignName;
        tracked.GenesysCampaignId = batch.GenesysCampaignId; tracked.CampaignAction = batch.CampaignAction;
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<CollectionsDispatchItem>> GetBatchItemsAsync(long batchId, CancellationToken ct) =>
        await db.CollectionsDispatchItems.AsNoTracking().Where(i => i.CollectionsGenesysBatchId == batchId).OrderBy(i => i.BatchPosition).ToListAsync(ct);

    public async Task<IReadOnlyList<CollectionsDispatchItem>> GetItemsAsync(long dispatchId, CancellationToken ct) =>
        await db.CollectionsDispatchItems.AsNoTracking().Where(i => i.CollectionsDispatchId == dispatchId).OrderBy(i => i.RecordKey).ToListAsync(ct);

    public async Task UpdateItemsAsync(IReadOnlyCollection<CollectionsDispatchItem> items, CancellationToken ct)
    {
        foreach (var chunk in items.Chunk(500))
        {
            var ids = chunk.Select(i => i.CollectionsDispatchItemId).ToList();
            var tracked = await db.CollectionsDispatchItems.Where(i => ids.Contains(i.CollectionsDispatchItemId)).ToDictionaryAsync(i => i.CollectionsDispatchItemId, ct);
            foreach (var item in chunk)
            {
                var target = tracked[item.CollectionsDispatchItemId];
                target.Status = item.Status; target.StatusReason = Cut(item.StatusReason);
                target.CollectionsGenesysBatchId = item.CollectionsGenesysBatchId; target.BatchPosition = item.BatchPosition;
                target.GenesysContactId = item.GenesysContactId; target.UploadedAtUtc = item.UploadedAtUtc;
                target.VoiceEligible = item.VoiceEligible; target.SuppressionStatus = item.SuppressionStatus;
                target.SuppressedAtUtc = item.SuppressedAtUtc; target.SuppressionError = Cut(item.SuppressionError);
                target.BalanceCheckedAtUtc = item.BalanceCheckedAtUtc;
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }

    public async Task<IReadOnlyList<UploadedContactView>> GetSuppressionCandidatesAsync(DateTime sinceUtc, int take, CancellationToken ct)
    {
        var rows = await (from i in db.CollectionsDispatchItems.AsNoTracking()
                          join b in db.CollectionsGenesysBatches.AsNoTracking() on i.CollectionsGenesysBatchId equals b.CollectionsGenesysBatchId
                          join d in db.CollectionsDispatches.AsNoTracking() on i.CollectionsDispatchId equals d.CollectionsDispatchId
                          where i.Status == DispatchItemStatus.UploadedToGenesys && i.GenesysContactId != null
                                && i.SuppressionStatus != ContactSuppressionStatus.Suppressed && i.UploadedAtUtc >= sinceUtc
                          orderby i.BalanceCheckedAtUtc, i.CollectionsDispatchItemId
                          select new { Item = i, b.ContactListId, d.FilterJson }).Take(take).ToListAsync(ct);
        return rows.Select(r => new UploadedContactView(r.Item, r.ContactListId, r.FilterJson)).ToList();
    }

    public Task SetDispatchPhaseAsync(long dispatchId, string phase, CancellationToken ct) =>
        db.CollectionsDispatches.Where(d => d.CollectionsDispatchId == dispatchId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Phase, phase), ct);

    public Task SetRevalidationMsAsync(long dispatchId, long ms, CancellationToken ct) =>
        db.CollectionsDispatches.Where(d => d.CollectionsDispatchId == dispatchId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.RevalidationMs, ms), ct);

    public async Task<OverlapPage> GetOverlapsAsync(long runId, int skip, int take, CancellationToken ct)
    {
        var flagged = db.CollectionsReviewRecords.AsNoTracking().Where(r => r.CollectionsReviewRunId == runId && r.Phone != ""
            && (EF.Functions.Like(r.Reasons, "%;SharedPhoneMultipleUnits;%") || EF.Functions.Like(r.Reasons, "%;ReminderTypeOverlap;%")));
        var phones = flagged.GroupBy(r => r.Phone);
        var total = await phones.CountAsync(ct);
        var page = await phones.OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Skip(skip).Take(take).Select(g => g.Key).ToListAsync(ct);
        var rows = page.Count == 0 ? [] : await flagged.Where(r => page.Contains(r.Phone))
            .OrderBy(r => r.Phone).ThenBy(r => r.TenantId).ThenBy(r => r.UnitCode).ThenBy(r => r.ReminderType)
            .Select(r => new OverlapRow(r.Phone, r.CompanyId, r.TenantId, r.CustomerName, r.UnitCode, r.ReminderType.ToString(), r.RemainingAmount, r.Currency, r.Reasons))
            .ToListAsync(ct);
        return new OverlapPage(total, page.Select(p => new OverlapGroup(p, rows.Where(r => r.Phone == p).ToList())).ToList());
    }

    private static string? Cut(string? text) => text is { Length: > 500 } ? text[..500] : text;

    internal static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqlException { Number: 2601 or 2627 }) return true;
            // SQLite (used by the relational tests) reports unique violations by message.
            if (inner.GetType().Name == "SqliteException" && inner.Message.Contains("UNIQUE constraint failed", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
