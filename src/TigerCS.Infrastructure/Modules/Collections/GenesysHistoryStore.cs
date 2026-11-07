using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.Collections.History;
using TigerCS.Application.Modules.Collections.Review;
using TigerCS.Domain.Modules.Collections.Review;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.Collections;

/// <summary>
/// Read-only EF queries behind the Genesys Campaign History. Every filter, count, sort and page runs in the database; the per-submission figures are computed with ONE grouped query over the
/// items of the submissions in question (the page, or the filtered set for the summary), never one query per row. Nothing here tracks, writes or calls anything external.
/// </summary>
public sealed class GenesysHistoryStore(TigerCsDbContext db) : IGenesysHistoryStore
{
    private static readonly Guid AutoActor = DispatchService.AutoActorId;

    /// <summary>Submitted = assigned to a Genesys batch and neither excluded nor released before the upload. Must match <see cref="GenesysUploadStateRules"/>.</summary>
    private static bool IsSubmitted(CollectionsDispatchItem i) =>
        i.CollectionsGenesysBatchId != null && i.Status != DispatchItemStatus.Excluded && i.Status != DispatchItemStatus.Released;

    // ---------------------------------------------------------------- the list

    public async Task<SubmissionPage> ListSubmissionsAsync(GenesysHistorySpec spec, int skip, int take, CancellationToken ct)
    {
        var filtered = Filter(db.CollectionsDispatches.AsNoTracking(), spec);
        var total = await filtered.CountAsync(ct);

        var page = await filtered.OrderByDescending(d => d.InitiatedAtUtc).ThenByDescending(d => d.CollectionsDispatchId).Skip(skip).Take(take)
            .Select(d => new
            {
                d.CollectionsDispatchId, d.PublicId, d.InitiatedAtUtc, d.StartedAtUtc, d.CompletedAtUtc, d.Status, d.StatusReason, d.Phase,
                d.InitiatedByEmployeeId, d.ApprovedCount, d.ApprovedTotalsJson, d.ExcludedAtDispatchCount
            }).ToListAsync(ct);
        var ids = page.Select(p => p.CollectionsDispatchId).ToList();

        var counts = await CountsAsync(db.CollectionsDispatchItems.AsNoTracking().Where(i => ids.Contains(i.CollectionsDispatchId)), ct);
        var types = (await db.CollectionsDispatchItems.AsNoTracking().Where(i => ids.Contains(i.CollectionsDispatchId))
            .Select(i => new { i.CollectionsDispatchId, i.ReminderType }).Distinct().ToListAsync(ct))
            .GroupBy(t => t.CollectionsDispatchId).ToDictionary(g => g.Key, g => (IReadOnlyList<CampaignReminderType>)g.Select(t => t.ReminderType).ToList());
        var batches = (await db.CollectionsGenesysBatches.AsNoTracking().Where(b => ids.Contains(b.CollectionsDispatchId)).OrderBy(b => b.Sequence)
            .Select(b => new { b.CollectionsDispatchId, Row = new SubmissionBatchRow(b.ReminderType, b.ContactListId, b.GenesysCampaignId, b.RequestHash, b.Status, b.Error, b.StartedAtUtc) })
            .ToListAsync(ct))
            .GroupBy(b => b.CollectionsDispatchId).ToDictionary(g => g.Key, g => (IReadOnlyList<SubmissionBatchRow>)g.Select(b => b.Row).ToList());

        var items = page.Select(p => new SubmissionRow(
            p.CollectionsDispatchId, p.PublicId, p.InitiatedAtUtc, p.StartedAtUtc, p.CompletedAtUtc, p.Status, p.StatusReason, p.Phase, p.InitiatedByEmployeeId,
            p.ApprovedCount, p.ApprovedTotalsJson, p.ExcludedAtDispatchCount, counts.GetValueOrDefault(p.CollectionsDispatchId, SubmissionCounts.Empty),
            types.GetValueOrDefault(p.CollectionsDispatchId, []), batches.GetValueOrDefault(p.CollectionsDispatchId, []))).ToList();

        // The summary covers the whole filtered set: its dispatch status plus the same grouped counts, as narrow rows.
        var statuses = await filtered.Select(d => new { d.CollectionsDispatchId, d.Status }).ToListAsync(ct);
        var allIds = filtered.Select(d => d.CollectionsDispatchId);
        var allCounts = await CountsAsync(db.CollectionsDispatchItems.AsNoTracking().Where(i => allIds.Contains(i.CollectionsDispatchId)), ct);
        var summary = statuses.Select(s => new SummaryRow(s.Status, allCounts.GetValueOrDefault(s.CollectionsDispatchId, SubmissionCounts.Empty))).ToList();
        return new(total, items, summary);
    }

    private static async Task<Dictionary<long, SubmissionCounts>> CountsAsync(IQueryable<CollectionsDispatchItem> items, CancellationToken ct)
    {
        var rows = await items.GroupBy(i => i.CollectionsDispatchId).Select(g => new
        {
            Id = g.Key,
            Approved = g.Count(),
            Submitted = g.Count(i => i.CollectionsGenesysBatchId != null && i.Status != DispatchItemStatus.Excluded && i.Status != DispatchItemStatus.Released),
            Uploaded = g.Count(i => i.CollectionsGenesysBatchId != null && i.Status == DispatchItemStatus.UploadedToGenesys),
            Failed = g.Count(i => i.CollectionsGenesysBatchId != null && i.Status == DispatchItemStatus.Failed),
            Unconfirmed = g.Count(i => i.CollectionsGenesysBatchId != null && i.Status == DispatchItemStatus.UnknownOutcome),
            Waiting = g.Count(i => i.CollectionsGenesysBatchId != null && i.Status == DispatchItemStatus.Approved),
            Excluded = g.Count(i => i.Status == DispatchItemStatus.Excluded),
            Released = g.Count(i => i.Status == DispatchItemStatus.Released),
            Other = g.Count(i => i.CollectionsGenesysBatchId != null && i.Status != DispatchItemStatus.Excluded && i.Status != DispatchItemStatus.Released && i.Currency != "AED"),
            Amount = g.Where(i => i.CollectionsGenesysBatchId != null && i.Status != DispatchItemStatus.Excluded && i.Status != DispatchItemStatus.Released && i.Currency == "AED").Sum(i => (decimal?)i.Amount) ?? 0m,
            ApprovedAmount = g.Where(i => i.Currency == "AED").Sum(i => (decimal?)i.Amount) ?? 0m
        }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => new SubmissionCounts(r.Approved, r.Submitted, r.Uploaded, r.Failed, r.Unconfirmed, r.Waiting, r.Excluded, r.Released, r.Amount, r.Other, r.ApprovedAmount));
    }

    private static IQueryable<CollectionsDispatch> Filter(IQueryable<CollectionsDispatch> q, GenesysHistorySpec s)
    {
        if (s.FromUtc is { } from) q = q.Where(d => d.InitiatedAtUtc >= from);
        if (s.ToUtcExclusive is { } to) q = q.Where(d => d.InitiatedAtUtc < to);
        if (s.EmployeeId is { } employee) q = q.Where(d => d.InitiatedByEmployeeId == employee);
        if (s.Mode == "Auto") q = q.Where(d => d.InitiatedByEmployeeId == AutoActor);
        else if (s.Mode == "Manual") q = q.Where(d => d.InitiatedByEmployeeId != AutoActor);
        if (s.Stage is { } stage) q = q.Where(d => d.Items.Any(i => i.ReminderType == stage));
        if (s.ListId is { } list) q = q.Where(d => d.Batches.Any(b => b.ContactListId == list));
        if (!string.IsNullOrEmpty(s.Search))
        {
            var text = s.Search;
            q = q.Where(d => d.Items.Any(i => i.UnitCode.Contains(text) || i.CustomerName.Contains(text) || i.GenesysContactId == text) || d.Batches.Any(b => b.GenesysCampaignId == text));
        }
        return s.Status is { } status ? q.Where(StatusPredicate(status)) : q;
    }

    /// <summary>
    /// <see cref="GenesysUploadStateRules.Classify"/> as a SQL predicate (a test runs both over every combination). "Finished" = the dispatch is no longer queued, revalidating or sending;
    /// "uncertain" = submitted records whose outcome is unknown or still waiting after the dispatch ended.
    /// </summary>
    private static System.Linq.Expressions.Expression<Func<CollectionsDispatch, bool>> StatusPredicate(string status) => status switch
    {
        GenesysUploadStates.Pending => d => d.Status == DispatchStatus.Queued || d.Status == DispatchStatus.Revalidating,
        GenesysUploadStates.Uploading => d => d.Status == DispatchStatus.Sending,
        GenesysUploadStates.NotSent => d => d.Status != DispatchStatus.Queued && d.Status != DispatchStatus.Revalidating && d.Status != DispatchStatus.Sending
            && !d.Items.Any(i => i.CollectionsGenesysBatchId != null && i.Status != DispatchItemStatus.Excluded && i.Status != DispatchItemStatus.Released),
        GenesysUploadStates.Uploaded => d => d.Status != DispatchStatus.Queued && d.Status != DispatchStatus.Revalidating && d.Status != DispatchStatus.Sending
            && d.Items.Any(i => i.CollectionsGenesysBatchId != null && i.Status == DispatchItemStatus.UploadedToGenesys)
            && !d.Items.Any(i => i.CollectionsGenesysBatchId != null && (i.Status == DispatchItemStatus.Failed || i.Status == DispatchItemStatus.UnknownOutcome || i.Status == DispatchItemStatus.Approved)),
        GenesysUploadStates.Partial => d => d.Status != DispatchStatus.Queued && d.Status != DispatchStatus.Revalidating && d.Status != DispatchStatus.Sending
            && d.Items.Any(i => i.CollectionsGenesysBatchId != null && i.Status == DispatchItemStatus.UploadedToGenesys)
            && d.Items.Any(i => i.CollectionsGenesysBatchId != null && (i.Status == DispatchItemStatus.Failed || i.Status == DispatchItemStatus.UnknownOutcome || i.Status == DispatchItemStatus.Approved)),
        GenesysUploadStates.Unknown => d => d.Status != DispatchStatus.Queued && d.Status != DispatchStatus.Revalidating && d.Status != DispatchStatus.Sending
            && !d.Items.Any(i => i.CollectionsGenesysBatchId != null && i.Status == DispatchItemStatus.UploadedToGenesys)
            && d.Items.Any(i => i.CollectionsGenesysBatchId != null && (i.Status == DispatchItemStatus.UnknownOutcome || i.Status == DispatchItemStatus.Approved)),
        GenesysUploadStates.Failed => d => d.Status != DispatchStatus.Queued && d.Status != DispatchStatus.Revalidating && d.Status != DispatchStatus.Sending
            && d.Items.Any(i => i.CollectionsGenesysBatchId != null && i.Status == DispatchItemStatus.Failed)
            && !d.Items.Any(i => i.CollectionsGenesysBatchId != null && (i.Status == DispatchItemStatus.UploadedToGenesys || i.Status == DispatchItemStatus.UnknownOutcome || i.Status == DispatchItemStatus.Approved)),
        _ => d => false
    };

    // ---------------------------------------------------------------- one submission

    public async Task<SubmissionDetailRow?> GetSubmissionAsync(Guid publicId, CancellationToken ct)
    {
        var d = await db.CollectionsDispatches.AsNoTracking().Where(x => x.PublicId == publicId)
            .Select(x => new
            {
                x.CollectionsDispatchId, x.PublicId, x.InitiatedAtUtc, x.StartedAtUtc, x.CompletedAtUtc, x.Status, x.StatusReason, x.Phase,
                x.InitiatedByEmployeeId, x.ApprovedCount, x.ApprovedTotalsJson, x.ExcludedAtDispatchCount
            }).FirstOrDefaultAsync(ct);
        if (d is null) return null;
        var id = d.CollectionsDispatchId;
        var counts = await CountsAsync(db.CollectionsDispatchItems.AsNoTracking().Where(i => i.CollectionsDispatchId == id), ct);
        var types = await db.CollectionsDispatchItems.AsNoTracking().Where(i => i.CollectionsDispatchId == id).Select(i => i.ReminderType).Distinct().ToListAsync(ct);
        var batches = await db.CollectionsGenesysBatches.AsNoTracking().Where(b => b.CollectionsDispatchId == id).OrderBy(b => b.Sequence).ToListAsync(ct);
        // Records that share a phone number with another record of the submission: each stays its own row; this only counts them.
        var shared = await db.CollectionsDispatchItems.AsNoTracking()
            .Where(i => i.CollectionsDispatchId == id && i.CollectionsGenesysBatchId != null && i.Status != DispatchItemStatus.Excluded && i.Status != DispatchItemStatus.Released)
            .GroupBy(i => i.Phone).Where(g => g.Count() > 1).Select(g => g.Count()).ToListAsync(ct);
        var row = new SubmissionRow(id, d.PublicId, d.InitiatedAtUtc, d.StartedAtUtc, d.CompletedAtUtc, d.Status, d.StatusReason, d.Phase, d.InitiatedByEmployeeId, d.ApprovedCount,
            d.ApprovedTotalsJson, d.ExcludedAtDispatchCount, counts.GetValueOrDefault(id, SubmissionCounts.Empty), types,
            batches.Select(b => new SubmissionBatchRow(b.ReminderType, b.ContactListId, b.GenesysCampaignId, b.RequestHash, b.Status, b.Error, b.StartedAtUtc)).ToList());
        return new(row, batches, shared.Sum());
    }

    public async Task<RecordPage> ListRecordsAsync(long dispatchId, GenesysRecordFilter filter, int skip, int take, CancellationToken ct)
    {
        var q = db.CollectionsDispatchItems.AsNoTracking().Where(i => i.CollectionsDispatchId == dispatchId);
        q = filter.Result switch
        {
            "Uploaded" => q.Where(i => i.CollectionsGenesysBatchId != null && i.Status == DispatchItemStatus.UploadedToGenesys),
            "Failed" => q.Where(i => i.CollectionsGenesysBatchId != null && i.Status == DispatchItemStatus.Failed),
            "Unknown" => q.Where(i => i.CollectionsGenesysBatchId != null && (i.Status == DispatchItemStatus.UnknownOutcome || i.Status == DispatchItemStatus.Approved)),
            "NotSent" => q.Where(i => i.Status == DispatchItemStatus.Excluded || i.Status == DispatchItemStatus.Released || (i.Status == DispatchItemStatus.Approved && i.CollectionsGenesysBatchId == null)),
            _ => q
        };
        if (!string.IsNullOrEmpty(filter.Search))
        {
            var text = filter.Search;
            q = q.Where(i => i.UnitCode.Contains(text) || i.CustomerName.Contains(text) || i.Phone.Contains(text) || i.Email.Contains(text) || i.GenesysContactId == text);
        }
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(i => i.CollectionsDispatchItemId).Skip(skip).Take(take).ToListAsync(ct);

        var phones = items.Select(i => i.Phone).Distinct().ToList();
        var sharedPhones = (await db.CollectionsDispatchItems.AsNoTracking()
            .Where(i => i.CollectionsDispatchId == dispatchId && i.CollectionsGenesysBatchId != null && i.Status != DispatchItemStatus.Excluded && i.Status != DispatchItemStatus.Released && phones.Contains(i.Phone))
            .GroupBy(i => i.Phone).Where(g => g.Count() > 1).Select(g => g.Key).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var started = await db.CollectionsGenesysBatches.AsNoTracking().Where(b => b.CollectionsDispatchId == dispatchId)
            .Select(b => new { b.CollectionsGenesysBatchId, b.StartedAtUtc }).ToListAsync(ct);
        var startedBy = started.ToDictionary(b => b.CollectionsGenesysBatchId, b => b.StartedAtUtc);
        return new(total, items.Select(i => new RecordRow(i, sharedPhones.Contains(i.Phone) && IsSubmitted(i),
            i.CollectionsGenesysBatchId is { } b && startedBy.TryGetValue(b, out var at) ? at : null)).ToList());
    }

    public async Task<IReadOnlyList<Guid>> ListSubmitterIdsAsync(CancellationToken ct) =>
        await db.CollectionsDispatches.AsNoTracking().Where(d => d.InitiatedByEmployeeId != AutoActor).Select(d => d.InitiatedByEmployeeId).Distinct().ToListAsync(ct);

    // ---------------------------------------------------------------- downloaded files (audit trail)

    public async Task<ExportAuditPage> ListExportAuditAsync(string action, DateTime? fromUtc, DateTime? toUtcExclusive, int skip, int take, CancellationToken ct)
    {
        var q = db.AuditEntries.AsNoTracking().Where(a => a.Action == action);
        if (fromUtc is { } from) q = q.Where(a => a.OccurredAtUtc >= from);
        if (toUtcExclusive is { } to) q = q.Where(a => a.OccurredAtUtc < to);
        var total = await q.CountAsync(ct);
        // Only the head of the entry is read: the end holds one-way customer fingerprints that this page never needs.
        var rows = await q.OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.AuditEntryId).Skip(skip).Take(take)
            .Select(a => new { a.AuditEntryId, a.OccurredAtUtc, a.ActorEmployeeId, Details = a.AfterValue == null ? null : a.AfterValue.Substring(0, 2000) }).ToListAsync(ct);
        return new(total, rows.Select(r => new ExportAuditRow(r.AuditEntryId, r.OccurredAtUtc, r.ActorEmployeeId, r.Details)).ToList());
    }
}
